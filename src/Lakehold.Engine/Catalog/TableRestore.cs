using System.Globalization;
using Lakehold.Engine.Execution;

namespace Lakehold.Engine.Catalog;

/// <summary>A reviewed or applied table-data restore from one DuckLake snapshot.</summary>
public sealed record TableRestoreResult(
    string Schema,
    string Table,
    long SnapshotId,
    long CurrentSnapshotId,
    long CurrentRowCount,
    long HistoricalRowCount,
    IReadOnlyList<string> RestoredColumns,
    IReadOnlyList<string> CurrentOnlyColumns,
    IReadOnlyList<string> HistoricalOnlyColumns,
    bool DryRun);

/// <summary>
///     Restores a table's rows from a snapshot while preserving the current table definition.
/// </summary>
/// <remarks>
///     <para>
///         <c>CREATE OR REPLACE TABLE AS SELECT</c> looks attractive but discards current
///         nullability and defaults. LakeHold instead stages the historical rows before deleting
///         anything, then inserts them into the existing table by the columns both versions share.
///         Current-only columns receive their current defaults (or NULL), historical-only columns
///         are reported and deliberately ignored, and every current constraint is enforced.
///     </para>
///     <para>
///         Apply runs under the Duckling's exclusive gate and inside one labelled transaction. Any
///         schema incompatibility or constraint failure therefore rolls the delete back before the
///         session is returned to another caller.
///     </para>
/// </remarks>
public static class TableRestore
{
    /// <summary>Plans or atomically applies a table-data restore.</summary>
    public static Task<TableRestoreResult> RunAsync(
        Duckling duckling,
        string schema,
        string table,
        long snapshotId,
        bool apply,
        long? expectedCurrentSnapshotId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(duckling);
        ArgumentOutOfRangeException.ThrowIfNegative(snapshotId);

        return apply
            ? duckling.InvokeLabelledAsync(
                $"lakehold restore: snapshot {snapshotId.ToString(CultureInfo.InvariantCulture)}",
                ct => ApplyUnguardedAsync(
                    duckling, schema, table, snapshotId, expectedCurrentSnapshotId, ct),
                cancellationToken)
            : duckling.InvokeAsync(
                ct => PlanUnguardedAsync(duckling, schema, table, snapshotId, dryRun: true, ct),
                cancellationToken);
    }

    private static async Task<TableRestoreResult> ApplyUnguardedAsync(
        Duckling duckling,
        string schema,
        string table,
        long snapshotId,
        long? expectedCurrentSnapshotId,
        CancellationToken cancellationToken)
    {
        var plan = await PlanUnguardedAsync(
                duckling, schema, table, snapshotId, dryRun: false, cancellationToken)
            .ConfigureAwait(false);

        if (expectedCurrentSnapshotId is null)
        {
            throw new ArgumentException(
                "Apply requires the current snapshot id from a reviewed restore plan.");
        }

        if (plan.CurrentSnapshotId != expectedCurrentSnapshotId.Value)
        {
            throw new ArgumentException(
                $"The catalog advanced from snapshot {expectedCurrentSnapshotId.Value.ToString(CultureInfo.InvariantCulture)} " +
                $"to {plan.CurrentSnapshotId.ToString(CultureInfo.InvariantCulture)} after this restore was reviewed. " +
                "Review a fresh plan; no rows were changed.");
        }

        var relation = $"{SqlIdentifier.QuoteName(schema)}.{SqlIdentifier.QuoteName(table)}";
        var stage = SqlIdentifier.QuoteName($"__lakehold_restore_{Guid.NewGuid():N}");

        if (plan.HistoricalRowCount > 0)
        {
            var columns = string.Join(", ", plan.RestoredColumns.Select(SqlIdentifier.QuoteName));
            await duckling
                .ExecuteUnguardedAsync(
                    $"CREATE TEMP TABLE {stage} AS SELECT {columns} FROM {relation} " +
                    $"AT (VERSION => {snapshotId.ToString(CultureInfo.InvariantCulture)})",
                    cancellationToken)
                .ConfigureAwait(false);

            await duckling
                .ExecuteUnguardedAsync($"DELETE FROM {relation}", cancellationToken)
                .ConfigureAwait(false);

            await duckling
                .ExecuteUnguardedAsync(
                    $"INSERT INTO {relation} ({columns}) SELECT {columns} FROM {stage}",
                    cancellationToken)
                .ConfigureAwait(false);

            await duckling
                .ExecuteUnguardedAsync($"DROP TABLE {stage}", cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            await duckling
                .ExecuteUnguardedAsync($"DELETE FROM {relation}", cancellationToken)
                .ConfigureAwait(false);
        }

        return plan;
    }

    private static async Task<TableRestoreResult> PlanUnguardedAsync(
        Duckling duckling,
        string schema,
        string table,
        long snapshotId,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        var (kind, currentColumns) = await TableInspector
            .ReadLogicalObjectUnguardedAsync(duckling, schema, table, cancellationToken)
            .ConfigureAwait(false);

        if (string.Equals(kind, "VIEW", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"'{schema}.{table}' is a view and cannot be restored as a table.");
        }

        var relation = $"{SqlIdentifier.QuoteName(schema)}.{SqlIdentifier.QuoteName(table)}";
        var version = snapshotId.ToString(CultureInfo.InvariantCulture);
        var historicalDescription = await duckling
            .ExecuteUnguardedAsync(
                $"DESCRIBE SELECT * FROM {relation} AT (VERSION => {version})",
                cancellationToken)
            .ConfigureAwait(false);

        var historicalColumns = historicalDescription.Rows
            .Select(row => Convert.ToString(row[0], CultureInfo.InvariantCulture) ?? string.Empty)
            .ToArray();
        var currentNames = currentColumns.Select(column => column.Name).ToArray();
        var historicalSet = historicalColumns.ToHashSet(StringComparer.Ordinal);
        var currentSet = currentNames.ToHashSet(StringComparer.Ordinal);

        var restored = currentNames.Where(historicalSet.Contains).ToArray();
        var currentOnly = currentNames.Where(name => !historicalSet.Contains(name)).ToArray();
        var historicalOnly = historicalColumns.Where(name => !currentSet.Contains(name)).ToArray();

        var counts = await duckling
            .ExecuteUnguardedAsync(
                $"SELECT " +
                $"(SELECT max(snapshot_id) FROM ducklake_snapshots(" +
                $"{SqlIdentifier.Literal(duckling.Catalog.CatalogName)})), " +
                $"(SELECT count(*) FROM {relation}), " +
                $"(SELECT count(*) FROM {relation} AT (VERSION => {version}))",
                cancellationToken)
            .ConfigureAwait(false);

        var currentSnapshot = Count(counts.Rows[0][0]);
        var currentRows = Count(counts.Rows[0][1]);
        var historicalRows = Count(counts.Rows[0][2]);
        if (historicalRows > 0 && restored.Length == 0)
        {
            throw new ArgumentException(
                $"Snapshot {version} and the current table '{schema}.{table}' have no columns in common. " +
                "No rows were changed.");
        }

        return new TableRestoreResult(
            schema,
            table,
            snapshotId,
            currentSnapshot,
            currentRows,
            historicalRows,
            restored,
            currentOnly,
            historicalOnly,
            dryRun);
    }

    private static long Count(object? value)
        => value is null ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
}

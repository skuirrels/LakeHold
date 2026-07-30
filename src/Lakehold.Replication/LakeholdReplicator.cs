using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lakehold.Client;

namespace Lakehold.Replication;

public sealed record ReplicaTableSelection(
    string Schema,
    string Table,
    ReplicaTableMode Mode,
    IReadOnlyList<string> KeyColumns);

public sealed record ReplicaSource(
    string SourceId,
    string Tenant,
    string Catalog,
    IReadOnlyList<ReplicaTableSelection> Tables,
    int PageSize = 5_000);

/// <summary>Bootstraps and advances one source-authoritative DuckDB mirror.</summary>
public sealed class LakeholdReplicator(
    LakeholdClient source,
    DuckDbReplica target,
    ReplicaSource configuration)
{
    public async Task<ReplicaCheckpoint> BootstrapAsync(CancellationToken cancellationToken)
    {
        var snapshot = await source
            .GetLatestSnapshotAsync(configuration.Tenant, configuration.Catalog, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("The source catalog has no snapshot to bootstrap.");
        var definitions = await ResolveDefinitionsAsync(cancellationToken).ConfigureAwait(false);
        var fingerprint = Fingerprint(definitions);
        var consumer = await source
            .RegisterConsumerAsync(
                configuration.Tenant,
                configuration.Catalog,
                configuration.SourceId,
                Math.Max(0, snapshot.SnapshotId - 1),
                cancellationToken)
            .ConfigureAwait(false);

        await target
            .BeginBootstrapAsync(configuration.SourceId, definitions, cancellationToken)
            .ConfigureAwait(false);
        foreach (var table in definitions)
        {
            var sourceCount = await SourceCountAsync(table, snapshot.SnapshotId, cancellationToken)
                .ConfigureAwait(false);
            long offset = 0;
            while (offset < sourceCount)
            {
                var page = await source
                    .ExecuteQueryAsync(
                        configuration.Tenant,
                        configuration.Catalog,
                        BootstrapSql(table, snapshot.SnapshotId, configuration.PageSize, offset),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (page.Truncated)
                {
                    throw new InvalidOperationException(
                        $"Bootstrap query for {table.Schema}.{table.Table} was truncated below its requested page size.");
                }

                ValidateColumns(table, page.Columns);
                if (page.Rows.Count == 0)
                {
                    throw new InvalidOperationException(
                        $"Bootstrap for {table.Schema}.{table.Table} ended at {offset:N0} of {sourceCount:N0} rows.");
                }

                await target.AppendBootstrapRowsAsync(table, page.Rows, cancellationToken).ConfigureAwait(false);
                offset += page.Rows.Count;
            }

            var targetCount = await target.CountRowsAsync(table, cancellationToken).ConfigureAwait(false);
            if (targetCount != sourceCount)
            {
                throw new InvalidOperationException(
                    $"Bootstrap verification failed for {table.Schema}.{table.Table}: "
                    + $"source={sourceCount:N0}, target={targetCount:N0}.");
            }
        }

        await target
            .CompleteBootstrapAsync(
                configuration.SourceId,
                configuration.Tenant,
                configuration.Catalog,
                snapshot.SnapshotId,
                fingerprint,
                cancellationToken)
            .ConfigureAwait(false);
        await source
            .AdvanceConsumerAsync(
                configuration.Tenant,
                configuration.Catalog,
                consumer.Id,
                snapshot.SnapshotId,
                cancellationToken)
            .ConfigureAwait(false);
        return await target.GetCheckpointAsync(configuration.SourceId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The target did not persist its bootstrap checkpoint.");
    }

    public async Task<ReplicaCheckpoint> ReplicateOnceAsync(CancellationToken cancellationToken)
    {
        var checkpoint = await target
            .GetCheckpointAsync(configuration.SourceId, cancellationToken)
            .ConfigureAwait(false)
            ?? await BootstrapAsync(cancellationToken).ConfigureAwait(false);
        var latest = await source
            .GetLatestSnapshotAsync(configuration.Tenant, configuration.Catalog, cancellationToken)
            .ConfigureAwait(false);
        if (latest is null || latest.SnapshotId <= checkpoint.LastAppliedSnapshot)
        {
            return checkpoint;
        }
        var consumer = await source
            .RegisterConsumerAsync(
                configuration.Tenant,
                configuration.Catalog,
                configuration.SourceId,
                checkpoint.LastAppliedSnapshot,
                cancellationToken)
            .ConfigureAwait(false);

        var definitions = await ResolveDefinitionsAsync(cancellationToken).ConfigureAwait(false);
        var fingerprint = Fingerprint(definitions);
        if (!string.Equals(checkpoint.SchemaFingerprint, fingerprint, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The source schema/configuration changed after bootstrap. This release fails closed; "
                + "apply an explicit migration or re-bootstrap the target.");
        }

        for (var snapshot = checkpoint.LastAppliedSnapshot + 1;
             snapshot <= latest.SnapshotId;
             snapshot++)
        {
            var allChanges = new List<ReplicaTableChanges>(definitions.Count);
            foreach (var table in definitions)
            {
                var changes = await DrainChangesAsync(table, snapshot, cancellationToken).ConfigureAwait(false);
                allChanges.Add(new ReplicaTableChanges(table, changes));
            }

            await target
                .ApplySnapshotAsync(
                    configuration.SourceId,
                    snapshot,
                    fingerprint,
                    allChanges,
                    cancellationToken)
                .ConfigureAwait(false);
            await source
                .AdvanceConsumerAsync(
                    configuration.Tenant,
                    configuration.Catalog,
                    consumer.Id,
                    snapshot,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return await target.GetCheckpointAsync(configuration.SourceId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The target checkpoint disappeared after apply.");
    }

    private async Task<IReadOnlyList<ReplicaTableDefinition>> ResolveDefinitionsAsync(
        CancellationToken cancellationToken)
    {
        var schemas = await source
            .GetSchemasAsync(configuration.Tenant, configuration.Catalog, cancellationToken)
            .ConfigureAwait(false);
        var definitions = new List<ReplicaTableDefinition>(configuration.Tables.Count);

        foreach (var selection in configuration.Tables)
        {
            var schema = schemas.FirstOrDefault(
                item => string.Equals(item.Name, selection.Schema, StringComparison.Ordinal));
            var table = schema?.Tables.FirstOrDefault(
                item => string.Equals(item.Name, selection.Table, StringComparison.Ordinal));
            if (table is null || !string.Equals(table.Kind, "table", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Source table {selection.Schema}.{selection.Table} does not exist as a base table.");
            }

            definitions.Add(new ReplicaTableDefinition(
                selection.Schema,
                selection.Table,
                [.. table.Columns.Select(column =>
                    new ReplicaColumn(column.Name, NormalizeType(column.DataType), column.IsNullable))],
                selection.Mode,
                selection.KeyColumns));
        }

        return definitions;
    }

    private async Task<IReadOnlyList<ReplicaChange>> DrainChangesAsync(
        ReplicaTableDefinition table,
        long snapshot,
        CancellationToken cancellationToken)
    {
        var changes = new List<ReplicaChange>();
        string? cursor = null;
        do
        {
            var page = await source
                .GetChangesAsync(
                    configuration.Tenant,
                    configuration.Catalog,
                    table.Schema,
                    table.Table,
                    snapshot,
                    configuration.PageSize,
                    cursor,
                    cancellationToken)
                .ConfigureAwait(false);
            changes.AddRange(page.Changes.Select(change =>
                new ReplicaChange(change.RowId, change.ChangeType, change.Row)));
            cursor = page.NextCursor;

            if (page.Truncated != (cursor is not null))
            {
                throw new InvalidOperationException(
                    $"Source page for {table.Schema}.{table.Table} reported an inconsistent continuation state.");
            }
        }
        while (cursor is not null);

        return changes;
    }

    private async Task<long> SourceCountAsync(
        ReplicaTableDefinition table,
        long snapshot,
        CancellationToken cancellationToken)
    {
        var page = await source
            .ExecuteQueryAsync(
                configuration.Tenant,
                configuration.Catalog,
                $"SELECT count(*) AS row_count FROM {Qualified(table)} "
                + $"AT (VERSION => {snapshot.ToString(CultureInfo.InvariantCulture)})",
                cancellationToken)
            .ConfigureAwait(false);
        if (page.Rows.Count != 1 || page.Rows[0].Length != 1)
        {
            throw new InvalidOperationException(
                $"Source count for {table.Schema}.{table.Table} returned an unexpected shape.");
        }

        var value = page.Rows[0][0];
        return value.ValueKind == JsonValueKind.String
            ? long.Parse(value.GetString()!, CultureInfo.InvariantCulture)
            : value.GetInt64();
    }

    private static string BootstrapSql(
        ReplicaTableDefinition table,
        long snapshot,
        int pageSize,
        long offset)
    {
        var orderColumns = table.Mode == ReplicaTableMode.Keyed
            ? table.KeyColumns
            : table.Columns.Select(column => column.Name).ToArray();
        return $"SELECT * FROM {Qualified(table)} "
               + $"AT (VERSION => {snapshot.ToString(CultureInfo.InvariantCulture)}) "
               + $"ORDER BY {string.Join(", ", orderColumns.Select(Quote))} "
               + $"LIMIT {Math.Clamp(pageSize, 1, 9_000).ToString(CultureInfo.InvariantCulture)} "
               + $"OFFSET {offset.ToString(CultureInfo.InvariantCulture)}";
    }

    private static void ValidateColumns(
        ReplicaTableDefinition table,
        IReadOnlyList<LakeholdColumn> actual)
    {
        if (actual.Count != table.Columns.Count
            || actual.Where((column, index) =>
                    !string.Equals(column.Name, table.Columns[index].Name, StringComparison.Ordinal))
                .Any())
        {
            throw new InvalidOperationException(
                $"Bootstrap query columns for {table.Schema}.{table.Table} no longer match its validated schema.");
        }
    }

    private static string Fingerprint(IReadOnlyList<ReplicaTableDefinition> definitions)
    {
        var canonical = JsonSerializer.Serialize(
            definitions
                .OrderBy(table => table.Schema, StringComparer.Ordinal)
                .ThenBy(table => table.Table, StringComparer.Ordinal));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static string NormalizeType(string dataType)
        => dataType switch
        {
            "INT8" => "BIGINT",
            "INT4" => "INTEGER",
            "INT2" => "SMALLINT",
            "STRING" => "VARCHAR",
            _ => dataType,
        };

    private static string Qualified(ReplicaTableDefinition table)
        => $"{Quote(table.Schema)}.{Quote(table.Table)}";

    private static string Quote(string identifier)
        => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
}

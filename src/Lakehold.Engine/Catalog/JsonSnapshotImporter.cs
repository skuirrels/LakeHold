using System.Diagnostics;
using Lakehold.Engine.Execution;

namespace Lakehold.Engine.Catalog;

/// <summary>Quality gates evaluated before a connector snapshot may replace its target table.</summary>
public sealed record JsonSnapshotQualityPolicy(
    long MinimumRows,
    IReadOnlyList<string> RequiredColumns,
    IReadOnlyList<string> NotNullColumns);

/// <summary>The published table and quality evidence produced by one full-snapshot refresh.</summary>
public sealed record JsonSnapshotImportResult(
    string Schema,
    string Table,
    long RowsPublished,
    IReadOnlyList<TabularImportedColumn> Columns,
    TimeSpan Elapsed);

/// <summary>
///     Atomically replaces a DuckLake table from disposable newline-delimited JSON scratch data.
/// </summary>
/// <remarks>
///     The source is first materialised into a temporary DuckDB table and validated. Only after all
///     quality gates pass is the durable target replaced, in the same labelled transaction. A failed
///     refresh therefore leaves the preceding data product intact.
/// </remarks>
public static class JsonSnapshotImporter
{
    public static Task<JsonSnapshotImportResult> ReplaceAsync(
        Duckling duckling,
        string filePath,
        string schema,
        string table,
        bool replaceExistingTarget,
        JsonSnapshotQualityPolicy quality,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(duckling);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(quality);
        if (quality.MinimumRows < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(quality), "Minimum rows cannot be negative.");
        }

        var validatedSchema = SqlIdentifier.Quote(schema);
        var validatedTable = SqlIdentifier.Quote(table);
        var required = ValidateColumns(quality.RequiredColumns);
        var notNull = ValidateColumns(quality.NotNullColumns);

        return duckling.InvokeLabelledAsync(
            $"lakehold connector refresh: {validatedSchema}.{validatedTable}",
            token => ReplaceUnguardedAsync(
                duckling,
                filePath,
                validatedSchema,
                validatedTable,
                replaceExistingTarget,
                quality.MinimumRows,
                required,
                notNull,
                token),
            cancellationToken);
    }

    private static async Task<JsonSnapshotImportResult> ReplaceUnguardedAsync(
        Duckling duckling,
        string filePath,
        string schema,
        string table,
        bool replaceExistingTarget,
        long minimumRows,
        string[] requiredColumns,
        string[] notNullColumns,
        CancellationToken cancellationToken)
    {
        var startedAt = Stopwatch.GetTimestamp();
        var staging = SqlIdentifier.QuoteName($"__lakehold_connector_{Guid.NewGuid():N}");
        var target = $"{SqlIdentifier.QuoteName(schema)}.{SqlIdentifier.QuoteName(table)}";
        var path = SqlIdentifier.Literal(filePath);

        try
        {
            await duckling.ExecuteUnguardedAsync(
                    $"CREATE TEMP TABLE {staging} AS SELECT * FROM read_json_auto({path}, format = 'newline_delimited')",
                    cancellationToken)
                .ConfigureAwait(false);

            var description = await duckling.ExecuteUnguardedAsync($"DESCRIBE {staging}", cancellationToken)
                .ConfigureAwait(false);
            var columns = description.Rows
                .Select(row => new TabularImportedColumn(Text(row[0]), Text(row[1])))
                .ToArray();
            var names = columns.Select(column => column.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

            var missing = requiredColumns.Concat(notNullColumns)
                .Where(column => !names.Contains(column))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (missing.Length > 0)
            {
                throw new JsonSnapshotQualityException(
                    $"The connector snapshot is missing required columns: {string.Join(", ", missing)}.");
            }

            if (!replaceExistingTarget)
            {
                var existing = await duckling.ExecuteUnguardedAsync(
                        "SELECT count(*) FROM information_schema.tables "
                        + $"WHERE table_schema = {SqlIdentifier.Literal(schema)} "
                        + $"AND table_name = {SqlIdentifier.Literal(table)}",
                        cancellationToken)
                    .ConfigureAwait(false);
                if (Count(existing.Rows.Single()[0]) > 0)
                {
                    throw new JsonSnapshotTargetConflictException(
                        $"Target table '{schema}.{table}' already exists and is not owned by this connector.");
                }
            }

            var aggregates = Enumerable.Repeat("count(*)", 1)
                .Concat(notNullColumns.Select(column =>
                    $"count(*) FILTER (WHERE {SqlIdentifier.QuoteName(column)} IS NULL)"));
            var qualityResult = await duckling.ExecuteUnguardedAsync(
                    $"SELECT {string.Join(", ", aggregates)} FROM {staging}",
                    cancellationToken)
                .ConfigureAwait(false);
            var qualityRow = qualityResult.Rows.Single();
            var rowCount = Count(qualityRow[0]);
            if (rowCount < minimumRows)
            {
                throw new JsonSnapshotQualityException(
                    $"The connector snapshot contains {rowCount} rows; at least {minimumRows} are required.");
            }

            for (var index = 0; index < notNullColumns.Length; index++)
            {
                var nullCount = Count(qualityRow[index + 1]);
                if (nullCount > 0)
                {
                    throw new JsonSnapshotQualityException(
                        $"Column '{notNullColumns[index]}' contains {nullCount} null values.");
                }
            }

            var publication = replaceExistingTarget ? "CREATE OR REPLACE TABLE" : "CREATE TABLE";
            await duckling.ExecuteUnguardedAsync(
                    $"{publication} {target} AS SELECT * FROM {staging}",
                    cancellationToken)
                .ConfigureAwait(false);

            return new JsonSnapshotImportResult(
                schema,
                table,
                rowCount,
                columns,
                Stopwatch.GetElapsedTime(startedAt));
        }
        catch (DuckDB.NET.Data.DuckDBException)
        {
            // DuckDB diagnostics can include record values and the node-local scratch path. Keep
            // that untrusted context out of query history, traces, connector runs, and API output.
            throw new JsonSnapshotImportException(
                "DuckDB could not import the connector snapshot. Confirm that every record is a compatible JSON object.");
        }
        finally
        {
            try
            {
                await duckling.ExecuteUnguardedAsync($"DROP TABLE IF EXISTS {staging}", CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch
            {
                // The enclosing transaction rollback also removes the temporary table. Cleanup must
                // never replace the quality or publication error that caused the refresh to fail.
            }
        }
    }

    private static string[] ValidateColumns(IReadOnlyList<string> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        return columns
            .Select(column => SqlIdentifier.Quote(column))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string Text(object? value) =>
        Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;

    private static long Count(object? value) => Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>A source snapshot that failed its declared data contract.</summary>
public sealed class JsonSnapshotQualityException(string message) : InvalidOperationException(message);

/// <summary>A safe import failure that contains neither source records nor scratch paths.</summary>
public sealed class JsonSnapshotImportException(string message) : Exception(message);

/// <summary>Raised when first publication would overwrite a table not owned by the connector.</summary>
public sealed class JsonSnapshotTargetConflictException(string message) : InvalidOperationException(message);

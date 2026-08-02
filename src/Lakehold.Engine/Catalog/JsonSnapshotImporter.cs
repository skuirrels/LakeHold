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
        => ReplaceAsync(
            duckling,
            filePath,
            schema,
            table,
            replaceExistingTarget,
            quality,
            DataConnectorSchemaBehavior.Reject,
            ownershipMarker: null,
            cancellationToken);

    public static Task<JsonSnapshotImportResult> ReplaceAsync(
        Duckling duckling,
        string filePath,
        string schema,
        string table,
        bool replaceExistingTarget,
        JsonSnapshotQualityPolicy quality,
        DataConnectorSchemaBehavior schemaBehavior,
        CancellationToken cancellationToken) => ReplaceAsync(
        duckling,
        filePath,
        schema,
        table,
        replaceExistingTarget,
        quality,
        schemaBehavior,
        ownershipMarker: null,
        cancellationToken);

    public static Task<JsonSnapshotImportResult> ReplaceAsync(
        Duckling duckling,
        string filePath,
        string schema,
        string table,
        bool replaceExistingTarget,
        JsonSnapshotQualityPolicy quality,
        DataConnectorSchemaBehavior schemaBehavior,
        string? ownershipMarker,
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
                schemaBehavior,
                ownershipMarker,
                token),
            cancellationToken);
    }

    /// <summary>Atomically applies a keyed delta. Replaying the same records is idempotent.</summary>
    public static Task<JsonSnapshotImportResult> UpsertAsync(
        Duckling duckling,
        string filePath,
        string schema,
        string table,
        bool targetProvisioned,
        IReadOnlyList<string> keyColumns,
        JsonSnapshotQualityPolicy quality,
        DataConnectorSchemaBehavior schemaBehavior,
        CancellationToken cancellationToken) => UpsertAsync(
        duckling,
        filePath,
        schema,
        table,
        targetProvisioned,
        keyColumns,
        quality,
        schemaBehavior,
        ownershipMarker: null,
        cancellationToken);

    public static Task<JsonSnapshotImportResult> UpsertAsync(
        Duckling duckling,
        string filePath,
        string schema,
        string table,
        bool targetProvisioned,
        IReadOnlyList<string> keyColumns,
        JsonSnapshotQualityPolicy quality,
        DataConnectorSchemaBehavior schemaBehavior,
        string? ownershipMarker,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(duckling);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(keyColumns);
        ArgumentNullException.ThrowIfNull(quality);
        var keys = ValidateColumns(keyColumns);
        if (keys.Length == 0)
        {
            throw new ArgumentException("Incremental publication requires at least one key column.", nameof(keyColumns));
        }

        var validatedSchema = SqlIdentifier.Quote(schema);
        var validatedTable = SqlIdentifier.Quote(table);
        return duckling.InvokeLabelledAsync(
            $"lakehold connector incremental refresh: {validatedSchema}.{validatedTable}",
            token => UpsertUnguardedAsync(
                duckling,
                filePath,
                validatedSchema,
                validatedTable,
                targetProvisioned,
                keys,
                quality.MinimumRows,
                ValidateColumns(quality.RequiredColumns),
                ValidateColumns(quality.NotNullColumns),
                schemaBehavior,
                ownershipMarker,
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
        DataConnectorSchemaBehavior schemaBehavior,
        string? ownershipMarker,
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

            var targetExists = await TargetExistsAsync(duckling, schema, table, cancellationToken)
                .ConfigureAwait(false);
            if (!replaceExistingTarget && targetExists
                && !await HasOwnershipMarkerAsync(
                        duckling,
                        schema,
                        table,
                        ownershipMarker,
                        cancellationToken)
                    .ConfigureAwait(false))
            {
                throw new JsonSnapshotTargetConflictException(
                    $"Target table '{schema}.{table}' already exists and is not owned by this connector.");
            }
            if (targetExists)
            {
                await EnsureCompatibleSchemaAsync(
                        duckling,
                        target,
                        columns,
                        schemaBehavior,
                        allowAddColumns: false,
                        cancellationToken)
                    .ConfigureAwait(false);
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

            var publication = replaceExistingTarget || targetExists
                ? "CREATE OR REPLACE TABLE"
                : "CREATE TABLE";
            await duckling.ExecuteUnguardedAsync(
                    $"{publication} {target} AS SELECT * FROM {staging}",
                    cancellationToken)
                .ConfigureAwait(false);
            await ApplyOwnershipMarkerAsync(duckling, target, ownershipMarker, cancellationToken)
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

    private static async Task<JsonSnapshotImportResult> UpsertUnguardedAsync(
        Duckling duckling,
        string filePath,
        string schema,
        string table,
        bool targetProvisioned,
        string[] keyColumns,
        long minimumRows,
        string[] requiredColumns,
        string[] notNullColumns,
        DataConnectorSchemaBehavior schemaBehavior,
        string? ownershipMarker,
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
            var missing = requiredColumns.Concat(notNullColumns).Concat(keyColumns)
                .Where(column => !names.Contains(column))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (missing.Length > 0)
            {
                throw new JsonSnapshotQualityException(
                    $"The connector delta is missing required columns: {string.Join(", ", missing)}.");
            }

            var nullCheckedColumns = notNullColumns.Concat(keyColumns)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var aggregates = Enumerable.Repeat("count(*)", 1)
                .Concat(nullCheckedColumns.Select(column =>
                    $"count(*) FILTER (WHERE {SqlIdentifier.QuoteName(column)} IS NULL)"));
            var qualityResult = await duckling.ExecuteUnguardedAsync(
                    $"SELECT {string.Join(", ", aggregates)} FROM {staging}",
                    cancellationToken)
                .ConfigureAwait(false);
            var qualityRow = qualityResult.Rows.Single();
            var rowCount = Count(qualityRow[0]);
            for (var index = 0; index < nullCheckedColumns.Length; index++)
            {
                var nullCount = Count(qualityRow[index + 1]);
                if (nullCount > 0)
                {
                    throw new JsonSnapshotQualityException(
                        $"Column '{nullCheckedColumns[index]}' contains {nullCount} null values.");
                }
            }

            var groupedKeys = string.Join(", ", keyColumns.Select(SqlIdentifier.QuoteName));
            var duplicateKeys = await duckling.ExecuteUnguardedAsync(
                    $"SELECT count(*) FROM (SELECT {groupedKeys} FROM {staging} "
                    + $"GROUP BY {groupedKeys} HAVING count(*) > 1) AS duplicate_keys",
                    cancellationToken)
                .ConfigureAwait(false);
            if (Count(duplicateKeys.Rows.Single()[0]) > 0)
            {
                throw new JsonSnapshotQualityException(
                    "The connector delta contains duplicate incremental keys.");
            }

            var targetExists = await TargetExistsAsync(duckling, schema, table, cancellationToken)
                .ConfigureAwait(false);
            if (!targetProvisioned && targetExists
                && !await HasOwnershipMarkerAsync(
                        duckling,
                        schema,
                        table,
                        ownershipMarker,
                        cancellationToken)
                    .ConfigureAwait(false))
            {
                throw new JsonSnapshotTargetConflictException(
                    $"Target table '{schema}.{table}' already exists and is not owned by this connector.");
            }

            if (!targetProvisioned && !targetExists)
            {
                await duckling.ExecuteUnguardedAsync(
                        $"CREATE TABLE {target} AS SELECT * FROM {staging}",
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await EnsureCompatibleSchemaAsync(
                        duckling,
                        target,
                        columns,
                        schemaBehavior,
                        allowAddColumns: true,
                        cancellationToken)
                    .ConfigureAwait(false);
                var match = string.Join(" AND ", keyColumns.Select(key =>
                    $"target.{SqlIdentifier.QuoteName(key)} IS NOT DISTINCT FROM delta.{SqlIdentifier.QuoteName(key)}"));
                await duckling.ExecuteUnguardedAsync(
                        $"DELETE FROM {target} AS target USING {staging} AS delta WHERE {match}",
                        cancellationToken)
                    .ConfigureAwait(false);
                await duckling.ExecuteUnguardedAsync(
                        $"INSERT INTO {target} BY NAME SELECT * FROM {staging}",
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            await ApplyOwnershipMarkerAsync(duckling, target, ownershipMarker, cancellationToken)
                .ConfigureAwait(false);

            var targetCount = await duckling.ExecuteUnguardedAsync(
                    $"SELECT count(*) FROM {target}",
                    cancellationToken)
                .ConfigureAwait(false);
            var publishedRows = Count(targetCount.Rows.Single()[0]);
            if (publishedRows < minimumRows)
            {
                throw new JsonSnapshotQualityException(
                    $"The connector target contains {publishedRows} rows; at least {minimumRows} are required.");
            }

            return new JsonSnapshotImportResult(
                schema,
                table,
                rowCount,
                columns,
                Stopwatch.GetElapsedTime(startedAt));
        }
        catch (DuckDB.NET.Data.DuckDBException)
        {
            throw new JsonSnapshotImportException(
                "DuckDB could not import the connector delta. Confirm that every record is a compatible JSON object.");
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
            }
        }
    }

    private static async Task EnsureCompatibleSchemaAsync(
        Duckling duckling,
        string target,
        IReadOnlyList<TabularImportedColumn> incoming,
        DataConnectorSchemaBehavior behavior,
        bool allowAddColumns,
        CancellationToken cancellationToken)
    {
        var description = await duckling.ExecuteUnguardedAsync($"DESCRIBE {target}", cancellationToken)
            .ConfigureAwait(false);
        var existing = description.Rows
            .Select(row => new TabularImportedColumn(Text(row[0]), Text(row[1])))
            .ToDictionary(column => column.Name, StringComparer.OrdinalIgnoreCase);
        var incomingByName = incoming.ToDictionary(column => column.Name, StringComparer.OrdinalIgnoreCase);
        var incompatible = existing.Values
            .Where(column => !incomingByName.TryGetValue(column.Name, out var next)
                             || !string.Equals(column.DataType, next.DataType, StringComparison.OrdinalIgnoreCase))
            .Select(column => column.Name)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (incompatible.Length > 0)
        {
            throw new JsonSnapshotQualityException(
                $"The connector schema is missing or changes existing columns: {string.Join(", ", incompatible)}.");
        }

        var added = incoming.Where(column => !existing.ContainsKey(column.Name)).ToArray();
        if (added.Length == 0)
        {
            return;
        }

        if (behavior != DataConnectorSchemaBehavior.Additive)
        {
            throw new JsonSnapshotQualityException(
                $"The connector schema adds columns while schema policy is reject: {string.Join(", ", added.Select(c => c.Name))}.");
        }

        if (allowAddColumns)
        {
            foreach (var column in added)
            {
                await duckling.ExecuteUnguardedAsync(
                        $"ALTER TABLE {target} ADD COLUMN {SqlIdentifier.QuoteName(column.Name)} {column.DataType}",
                        cancellationToken)
                    .ConfigureAwait(false);
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

    private static async Task<bool> TargetExistsAsync(
        Duckling duckling,
        string schema,
        string table,
        CancellationToken cancellationToken)
    {
        var existing = await duckling.ExecuteUnguardedAsync(
                "SELECT count(*) FROM information_schema.tables "
                + $"WHERE table_schema = {SqlIdentifier.Literal(schema)} "
                + $"AND table_name = {SqlIdentifier.Literal(table)}",
                cancellationToken)
            .ConfigureAwait(false);
        return Count(existing.Rows.Single()[0]) > 0;
    }

    private static async Task<bool> HasOwnershipMarkerAsync(
        Duckling duckling,
        string schema,
        string table,
        string? ownershipMarker,
        CancellationToken cancellationToken)
    {
        if (ownershipMarker is null)
        {
            return false;
        }

        var result = await duckling.ExecuteUnguardedAsync(
                "SELECT comment FROM duckdb_tables() "
                + $"WHERE schema_name = {SqlIdentifier.Literal(schema)} "
                + $"AND table_name = {SqlIdentifier.Literal(table)}",
                cancellationToken)
            .ConfigureAwait(false);
        return result.Rows.Count == 1
               && string.Equals(Text(result.Rows[0][0]), ownershipMarker, StringComparison.Ordinal);
    }

    private static Task ApplyOwnershipMarkerAsync(
        Duckling duckling,
        string target,
        string? ownershipMarker,
        CancellationToken cancellationToken) => ownershipMarker is null
        ? Task.CompletedTask
        : duckling.ExecuteUnguardedAsync(
            $"COMMENT ON TABLE {target} IS {SqlIdentifier.Literal(ownershipMarker)}",
            cancellationToken);

    private static string Text(object? value) =>
        Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;

    private static long Count(object? value) => Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Engine-level projection of the control-plane schema policy.</summary>
public enum DataConnectorSchemaBehavior
{
    Reject = 0,
    Additive = 1,
    MappedVersion = 2,
}

/// <summary>A source snapshot that failed its declared data contract.</summary>
public sealed class JsonSnapshotQualityException(string message) : InvalidOperationException(message);

/// <summary>A safe import failure that contains neither source records nor scratch paths.</summary>
public sealed class JsonSnapshotImportException(string message) : Exception(message);

/// <summary>Raised when first publication would overwrite a table not owned by the connector.</summary>
public sealed class JsonSnapshotTargetConflictException(string message) : InvalidOperationException(message);

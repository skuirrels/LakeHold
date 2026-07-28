using System.Globalization;
using Lakehold.Engine.Execution;

namespace Lakehold.Engine.Catalog;

/// <summary>Live summary statistics for one logical column.</summary>
public sealed record ColumnProfileInfo(
    string Name,
    string DataType,
    long RowCount,
    long NullCount,
    string? Minimum,
    string? Maximum,
    string? ApproxDistinct,
    string? Mean,
    string? StandardDeviation,
    string? FirstQuartile,
    string? Median,
    string? ThirdQuartile);

/// <summary>All columns in one table or view, profiled at one catalog snapshot.</summary>
public sealed record TableProfileInfo(
    string SchemaName,
    string TableName,
    long? SnapshotId,
    long RowCount,
    IReadOnlyList<ColumnProfileInfo> Columns);

/// <summary>One bounded frequency or range bucket.</summary>
public sealed record DistributionBucketInfo(
    string Label,
    string? LowerBound,
    string? UpperBound,
    long Count);

/// <summary>A bounded distribution for one column.</summary>
public sealed record ColumnDistributionInfo(
    string SchemaName,
    string TableName,
    string ColumnName,
    string DataType,
    long? SnapshotId,
    string Kind,
    long NullCount,
    bool Truncated,
    IReadOnlyList<DistributionBucketInfo> Buckets);

/// <summary>
///     Profiles the logical rows visible through DuckLake rather than adding physical file
///     statistics that can omit inlined rows and retain values removed by merge-on-read deletes.
/// </summary>
public static class ColumnProfiler
{
    private sealed record RelationColumn(string Name, string DataType);

    /// <summary>Profiles every column in one table or view.</summary>
    public static Task<TableProfileInfo> ReadAsync(
        Duckling duckling,
        string schemaName,
        string tableName,
        long? snapshotId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(duckling);
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaName);
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);

        return duckling.InvokeAsync(
            ct => ReadUnguardedAsync(duckling, schemaName, tableName, snapshotId, ct),
            cancellationToken);
    }

    /// <summary>Reads a bounded distribution for one column.</summary>
    public static Task<ColumnDistributionInfo> ReadDistributionAsync(
        Duckling duckling,
        string schemaName,
        string tableName,
        string columnName,
        long? snapshotId,
        int maxBuckets,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(duckling);
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaName);
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        ArgumentException.ThrowIfNullOrWhiteSpace(columnName);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBuckets, 1);

        return duckling.InvokeAsync(
            ct => ReadDistributionUnguardedAsync(
                duckling, schemaName, tableName, columnName, snapshotId, maxBuckets, ct),
            cancellationToken);
    }

    private static async Task<TableProfileInfo> ReadUnguardedAsync(
        Duckling duckling,
        string schemaName,
        string tableName,
        long? snapshotId,
        CancellationToken cancellationToken)
    {
        var logical = await TableInspector
            .ReadLogicalObjectUnguardedAsync(duckling, schemaName, tableName, cancellationToken)
            .ConfigureAwait(false);
        RefuseHistoricalView(logical.Kind, schemaName, tableName, snapshotId);

        var relation = Relation(schemaName, tableName, snapshotId);

        // SUMMARIZE returns the relation's schema as it exists at the requested snapshot. Using
        // information_schema here would use today's column names, so a later rename or add could
        // make an otherwise valid historical profile fail to bind.
        var summary = await duckling
            .ExecuteUnguardedAsync($"SUMMARIZE SELECT * FROM {relation}", cancellationToken)
            .ConfigureAwait(false);
        var summaryColumns = summary.Rows
            .Select(row => (Name: Text(row[0]), DataType: Text(row[1])))
            .ToArray();
        var nullTerms = summaryColumns
            .Select(column => $"count_if({SqlIdentifier.QuoteName(column.Name)} IS NULL)")
            .ToArray();
        var nullSql = nullTerms.Length == 0
            ? $"SELECT count(*) FROM {relation}"
            : $"SELECT count(*), {string.Join(", ", nullTerms)} FROM {relation}";

        // Exact null counts need a separate aggregate. SUMMARIZE reports a display percentage,
        // which is deliberately lossy and cannot be safely converted back into a count.
        var nulls = await duckling
            .ExecuteUnguardedAsync(nullSql, cancellationToken)
            .ConfigureAwait(false);
        var counts = nulls.Rows.Single();
        var rowCount = Number(counts[0]);
        var nullCounts = summaryColumns
            .Select((column, index) => (column.Name, Count: Number(counts[index + 1])))
            .ToDictionary(item => item.Name, item => item.Count, StringComparer.Ordinal);

        var columns = summary.Rows.Select(row => new ColumnProfileInfo(
            Text(row[0]),
            Text(row[1]),
            rowCount,
            nullCounts.GetValueOrDefault(Text(row[0])),
            OptionalText(row[2]),
            OptionalText(row[3]),
            OptionalText(row[4]),
            OptionalText(row[5]),
            OptionalText(row[6]),
            OptionalText(row[7]),
            OptionalText(row[8]),
            OptionalText(row[9]))).ToArray();

        return new TableProfileInfo(schemaName, tableName, snapshotId, rowCount, columns);
    }

    private static async Task<ColumnDistributionInfo> ReadDistributionUnguardedAsync(
        Duckling duckling,
        string schemaName,
        string tableName,
        string columnName,
        long? snapshotId,
        int maxBuckets,
        CancellationToken cancellationToken)
    {
        var logical = await TableInspector
            .ReadLogicalObjectUnguardedAsync(duckling, schemaName, tableName, cancellationToken)
            .ConfigureAwait(false);
        RefuseHistoricalView(logical.Kind, schemaName, tableName, snapshotId);

        var relation = Relation(schemaName, tableName, snapshotId);
        var relationColumns = await ReadRelationColumnsUnguardedAsync(
                duckling, relation, cancellationToken)
            .ConfigureAwait(false);
        var column = relationColumns.SingleOrDefault(
            c => string.Equals(c.Name, columnName, StringComparison.Ordinal))
            ?? throw new ArgumentException(
                $"Column '{columnName}' does not exist on '{schemaName}.{tableName}'.");

        var quotedColumn = SqlIdentifier.QuoteName(columnName);
        var nullResult = await duckling
            .ExecuteUnguardedAsync(
                $"SELECT count_if({quotedColumn} IS NULL) FROM {relation}",
                cancellationToken)
            .ConfigureAwait(false);
        var nullCount = Number(nullResult.Rows.Single()[0]);

        if (IsNumeric(column.DataType) || IsTemporal(column.DataType))
        {
            return await ReadRangeDistributionAsync(
                    duckling, schemaName, tableName, column, relation, quotedColumn,
                    snapshotId, nullCount, maxBuckets, cancellationToken)
                .ConfigureAwait(false);
        }

        if (IsCategorical(column.DataType))
        {
            return await ReadCategoricalDistributionAsync(
                    duckling, schemaName, tableName, column, relation, quotedColumn,
                    snapshotId, nullCount, maxBuckets, cancellationToken)
                .ConfigureAwait(false);
        }

        return new ColumnDistributionInfo(
            schemaName, tableName, columnName, column.DataType, snapshotId,
            Kind: "unsupported", nullCount, Truncated: false, Buckets: []);
    }

    private static async Task<ColumnDistributionInfo> ReadRangeDistributionAsync(
        Duckling duckling,
        string schemaName,
        string tableName,
        RelationColumn column,
        string relation,
        string quotedColumn,
        long? snapshotId,
        long nullCount,
        int maxBuckets,
        CancellationToken cancellationToken)
    {
        var numericValue = IsTemporal(column.DataType)
            ? $"epoch({quotedColumn})"
            : $"CAST({quotedColumn} AS DOUBLE)";
        var bucketExpression = maxBuckets == 1
            ? "0"
            : $"""
                CASE
                    WHEN NOT isfinite(numeric_value) THEN {maxBuckets - 1}
                    WHEN hi = lo THEN 0
                    ELSE least(
                        CASE WHEN has_non_finite THEN {maxBuckets - 2} ELSE {maxBuckets - 1} END,
                        greatest(
                            0,
                            CAST(floor(
                                (numeric_value - lo) / (hi - lo) *
                                CASE WHEN has_non_finite THEN {maxBuckets - 1} ELSE {maxBuckets} END
                            ) AS INTEGER)))
                END
                """;

        // Equal-width buckets communicate skew; equal-row quantile buckets would render nearly
        // identical bars and hide it. The displayed bounds come from the source type, while DOUBLE
        // is used only to assign a stable bucket index. NaN and infinities share one reserved bucket
        // so they remain visible without exceeding the caller's bucket ceiling.
        var sql = $"""
            WITH source AS (
                SELECT {quotedColumn} AS value, {numericValue} AS numeric_value
                FROM {relation}
                WHERE {quotedColumn} IS NOT NULL
            ),
            bounds AS (
                SELECT
                    min(numeric_value) FILTER (WHERE isfinite(numeric_value)) AS lo,
                    max(numeric_value) FILTER (WHERE isfinite(numeric_value)) AS hi,
                    coalesce(bool_or(NOT isfinite(numeric_value)), false) AS has_non_finite
                FROM source
            ),
            bucketed AS (
                SELECT
                    value,
                    numeric_value,
                    {bucketExpression} AS bucket
                FROM source
                CROSS JOIN bounds
            )
            SELECT
                bucket,
                CAST(min(value) FILTER (WHERE isfinite(numeric_value)) AS VARCHAR) AS lower_bound,
                CAST(max(value) FILTER (WHERE isfinite(numeric_value)) AS VARCHAR) AS upper_bound,
                count(*) AS value_count,
                count_if(NOT isfinite(numeric_value)) AS non_finite_count
            FROM bucketed
            GROUP BY bucket
            ORDER BY bucket
            """;

        var result = await duckling
            .ExecuteUnguardedAsync(sql, cancellationToken)
            .ConfigureAwait(false);
        var buckets = result.Rows.Select(row =>
        {
            var count = Number(row[3]);
            var nonFiniteCount = Number(row[4]);
            var lower = OptionalText(row[1]);
            var upper = OptionalText(row[2]);
            var label = nonFiniteCount switch
            {
                0 when lower == upper => lower ?? string.Empty,
                0 => $"{lower} – {upper}",
                _ when nonFiniteCount == count => "Non-finite",
                _ => "All values",
            };
            return new DistributionBucketInfo(
                label,
                nonFiniteCount == 0 ? lower : null,
                nonFiniteCount == 0 ? upper : null,
                count);
        }).ToArray();

        return new ColumnDistributionInfo(
            schemaName, tableName, column.Name, column.DataType, snapshotId,
            Kind: "range", nullCount, Truncated: false, buckets);
    }

    private static async Task<ColumnDistributionInfo> ReadCategoricalDistributionAsync(
        Duckling duckling,
        string schemaName,
        string tableName,
        RelationColumn column,
        string relation,
        string quotedColumn,
        long? snapshotId,
        long nullCount,
        int maxBuckets,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            SELECT CAST({quotedColumn} AS VARCHAR) AS value, count(*) AS value_count
            FROM {relation}
            WHERE {quotedColumn} IS NOT NULL
            GROUP BY {quotedColumn}
            ORDER BY value_count DESC, value
            LIMIT {maxBuckets + 1}
            """;

        var result = await duckling
            .ExecuteUnguardedAsync(sql, cancellationToken)
            .ConfigureAwait(false);
        var truncated = result.Rows.Count > maxBuckets;
        var buckets = result.Rows
            .Take(maxBuckets)
            .Select(row => new DistributionBucketInfo(
                Text(row[0]), LowerBound: null, UpperBound: null, Number(row[1])))
            .ToArray();

        return new ColumnDistributionInfo(
            schemaName, tableName, column.Name, column.DataType, snapshotId,
            Kind: "categorical", nullCount, truncated, buckets);
    }

    private static async Task<IReadOnlyList<RelationColumn>> ReadRelationColumnsUnguardedAsync(
        Duckling duckling,
        string relation,
        CancellationToken cancellationToken)
    {
        var description = await duckling
            .ExecuteUnguardedAsync($"DESCRIBE SELECT * FROM {relation}", cancellationToken)
            .ConfigureAwait(false);

        return description.Rows
            .Select(row => new RelationColumn(Text(row[0]), Text(row[1])))
            .ToArray();
    }

    private static string Relation(string schemaName, string tableName, long? snapshotId)
    {
        var relation = $"{SqlIdentifier.QuoteName(schemaName)}.{SqlIdentifier.QuoteName(tableName)}";
        return snapshotId is null
            ? relation
            : $"{relation} AT (VERSION => {snapshotId.Value.ToString(CultureInfo.InvariantCulture)})";
    }

    private static void RefuseHistoricalView(
        string kind,
        string schemaName,
        string tableName,
        long? snapshotId)
    {
        if (snapshotId is not null && string.Equals(kind, "VIEW", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"View '{schemaName}.{tableName}' cannot be profiled at a DuckLake snapshot.");
        }
    }

    private static bool IsNumeric(string dataType)
    {
        var type = dataType.ToUpperInvariant();
        return type.StartsWith("TINYINT", StringComparison.Ordinal)
            || type.StartsWith("SMALLINT", StringComparison.Ordinal)
            || type.StartsWith("INTEGER", StringComparison.Ordinal)
            || type.StartsWith("BIGINT", StringComparison.Ordinal)
            || type.StartsWith("HUGEINT", StringComparison.Ordinal)
            || type.StartsWith("UTINYINT", StringComparison.Ordinal)
            || type.StartsWith("USMALLINT", StringComparison.Ordinal)
            || type.StartsWith("UINTEGER", StringComparison.Ordinal)
            || type.StartsWith("UBIGINT", StringComparison.Ordinal)
            || type.StartsWith("UHUGEINT", StringComparison.Ordinal)
            || type.StartsWith("FLOAT", StringComparison.Ordinal)
            || type.StartsWith("DOUBLE", StringComparison.Ordinal)
            || type.StartsWith("REAL", StringComparison.Ordinal)
            || type.StartsWith("DECIMAL", StringComparison.Ordinal);
    }

    private static bool IsTemporal(string dataType)
    {
        var type = dataType.ToUpperInvariant();
        return type.StartsWith("DATE", StringComparison.Ordinal)
            || type.StartsWith("TIMESTAMP", StringComparison.Ordinal)
            || type.StartsWith("TIME", StringComparison.Ordinal);
    }

    private static bool IsCategorical(string dataType)
    {
        var type = dataType.ToUpperInvariant();
        return type.StartsWith("BOOLEAN", StringComparison.Ordinal)
            || type.StartsWith("VARCHAR", StringComparison.Ordinal)
            || type.StartsWith("CHAR", StringComparison.Ordinal)
            || type.StartsWith("ENUM", StringComparison.Ordinal)
            || type.StartsWith("UUID", StringComparison.Ordinal);
    }

    private static string Text(object? value) =>
        Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;

    private static string? OptionalText(object? value) =>
        value is null ? null : Convert.ToString(value, CultureInfo.InvariantCulture);

    private static long Number(object? value) =>
        Convert.ToInt64(value, CultureInfo.InvariantCulture);
}

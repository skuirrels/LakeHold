using System.Globalization;
using Lakehold.Engine.Execution;

namespace Lakehold.Engine.Catalog;

/// <summary>A logical column shown in table detail.</summary>
public sealed record TableDetailColumn(
    string Name,
    string DataType,
    bool IsNullable);

/// <summary>One column participating in a DuckLake partition specification.</summary>
public sealed record PartitionKeyInfo(
    int Position,
    string ColumnName,
    string Transform);

/// <summary>
///     A partition specification and the snapshot interval in which it applies.
/// </summary>
public sealed record PartitionSpecInfo(
    long PartitionId,
    long BeginSnapshot,
    long? EndSnapshot,
    IReadOnlyList<PartitionKeyInfo> Keys);

/// <summary>
///     One table or view's logical and physical detail.
/// </summary>
/// <param name="Storage">
///     Physical footprint for a base table, or null for a view. A view has columns and can be
///     profiled, but owns no Parquet files or partition specification.
/// </param>
public sealed record TableDetailInfo(
    string SchemaName,
    string TableName,
    string Kind,
    IReadOnlyList<TableDetailColumn> Columns,
    TableStorageInfo? Storage,
    IReadOnlyList<PartitionSpecInfo> PartitionSpecs,
    long? TargetFileSizeBytes);

/// <summary>
///     Reads the coherent logical and physical description of one table.
/// </summary>
/// <remarks>
///     The whole read holds the Duckling gate. Otherwise a schema or maintenance commit can land
///     between the column list, footprint, and partition specification and produce a response whose
///     sections never described the same catalog state.
/// </remarks>
public static class TableInspector
{
    /// <summary>Reads one table or view at the current catalog state.</summary>
    public static Task<TableDetailInfo> ReadAsync(
        Duckling duckling,
        string schemaName,
        string tableName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(duckling);
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaName);
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);

        return duckling.InvokeAsync(
            ct => ReadUnguardedAsync(duckling, schemaName, tableName, ct),
            cancellationToken);
    }

    internal static async Task<TableDetailInfo> ReadUnguardedAsync(
        Duckling duckling,
        string schemaName,
        string tableName,
        CancellationToken cancellationToken)
    {
        var logical = await ReadLogicalObjectUnguardedAsync(
                duckling, schemaName, tableName, cancellationToken)
            .ConfigureAwait(false);

        if (string.Equals(logical.Kind, "VIEW", StringComparison.OrdinalIgnoreCase))
        {
            return new TableDetailInfo(
                schemaName,
                tableName,
                logical.Kind,
                logical.Columns,
                Storage: null,
                PartitionSpecs: [],
                TargetFileSizeBytes: null);
        }

        var storage = await StorageBrowser
            .ReadTableUnguardedAsync(duckling, schemaName, tableName, cancellationToken)
            .ConfigureAwait(false);
        var tableStorage = storage.Tables.SingleOrDefault();
        if (tableStorage is null)
        {
            throw new ArgumentException(
                $"Table '{schemaName}.{tableName}' was found in the schema but not in DuckLake storage.");
        }

        var partitions = await ReadPartitionsUnguardedAsync(
                duckling, schemaName, tableName, cancellationToken)
            .ConfigureAwait(false);

        return new TableDetailInfo(
            schemaName,
            tableName,
            logical.Kind,
            logical.Columns,
            tableStorage,
            partitions,
            storage.TargetFileSizeBytes);
    }

    /// <summary>Reads the object kind and columns without acquiring the gate.</summary>
    internal static async Task<(string Kind, IReadOnlyList<TableDetailColumn> Columns)>
        ReadLogicalObjectUnguardedAsync(
            Duckling duckling,
            string schemaName,
            string tableName,
            CancellationToken cancellationToken)
    {
        var sql = $"""
            SELECT
                COALESCE(t.table_type, 'BASE TABLE') AS table_type,
                c.column_name,
                c.data_type,
                c.is_nullable
            FROM information_schema.columns AS c
            LEFT JOIN information_schema.tables AS t
                ON t.table_catalog = c.table_catalog
               AND t.table_schema = c.table_schema
               AND t.table_name = c.table_name
            WHERE c.table_catalog = {SqlIdentifier.Literal(duckling.Catalog.CatalogName)}
              AND c.table_schema = {SqlIdentifier.Literal(schemaName)}
              AND c.table_name = {SqlIdentifier.Literal(tableName)}
            ORDER BY c.ordinal_position
            """;

        var result = await duckling
            .ExecuteUnguardedAsync(sql, cancellationToken)
            .ConfigureAwait(false);

        if (result.Rows.Count == 0)
        {
            throw new ArgumentException($"Table or view '{schemaName}.{tableName}' does not exist.");
        }

        var kind = Text(result.Rows[0][0]);
        var columns = result.Rows
            .Select(row => new TableDetailColumn(
                Text(row[1]),
                Text(row[2]),
                string.Equals(Text(row[3]), "YES", StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        return (kind, columns);
    }

    private static async Task<IReadOnlyList<PartitionSpecInfo>> ReadPartitionsUnguardedAsync(
        Duckling duckling,
        string schemaName,
        string tableName,
        CancellationToken cancellationToken)
    {
        var metadata = await MetadataExporter
            .ResolveMetadataAliasAsync(duckling, cancellationToken)
            .ConfigureAwait(false);
        var meta = SqlIdentifier.Quote(metadata, nameof(metadata));

        // Column names are resolved as they stood when the partition specification began. A column
        // rename produces a new ducklake_column version with the same column id; joining only the
        // current version would rewrite history in the inspector.
        var sql = $"""
            SELECT
                pi.partition_id,
                pi.begin_snapshot,
                pi.end_snapshot,
                pc.partition_key_index,
                c.column_name,
                pc.transform
            FROM {meta}.ducklake_schema AS s
            JOIN {meta}.ducklake_table AS t
                ON t.schema_id = s.schema_id
               AND t.end_snapshot IS NULL
            JOIN {meta}.ducklake_partition_info AS pi
                ON pi.table_id = t.table_id
            JOIN {meta}.ducklake_partition_column AS pc
                ON pc.table_id = t.table_id
               AND pc.partition_id = pi.partition_id
            JOIN {meta}.ducklake_column AS c
                ON c.table_id = t.table_id
               AND c.column_id = pc.column_id
               AND c.begin_snapshot <= pi.begin_snapshot
               AND (c.end_snapshot IS NULL OR c.end_snapshot > pi.begin_snapshot)
            WHERE s.schema_name = {SqlIdentifier.Literal(schemaName)}
              AND s.end_snapshot IS NULL
              AND t.table_name = {SqlIdentifier.Literal(tableName)}
            ORDER BY pi.begin_snapshot DESC, pc.partition_key_index
            """;

        var result = await duckling
            .ExecuteUnguardedAsync(sql, cancellationToken)
            .ConfigureAwait(false);

        return result.Rows
            .GroupBy(row => Number(row[0]))
            .Select(group =>
            {
                var first = group.First();
                return new PartitionSpecInfo(
                    Number(first[0]),
                    Number(first[1]),
                    first[2] is null ? null : Number(first[2]),
                    [
                        .. group.Select(row => new PartitionKeyInfo(
                            checked((int)Number(row[3])),
                            Text(row[4]),
                            Text(row[5]))),
                    ]);
            })
            .OrderByDescending(spec => spec.BeginSnapshot)
            .ToArray();
    }

    private static string Text(object? value) =>
        Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;

    private static long Number(object? value) =>
        Convert.ToInt64(value, CultureInfo.InvariantCulture);
}

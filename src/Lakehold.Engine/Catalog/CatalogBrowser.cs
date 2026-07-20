using System.Globalization;
using Lakehold.Engine.Execution;

namespace Lakehold.Engine.Catalog;

/// <summary>A schema within a catalog.</summary>
public sealed record SchemaInfo(string Name, IReadOnlyList<TableInfo> Tables);

/// <summary>A table or view within a schema.</summary>
public sealed record TableInfo(string Name, string Kind, IReadOnlyList<ColumnInfo> Columns);

/// <summary>A column within a table.</summary>
public sealed record ColumnInfo(string Name, string DataType, bool IsNullable);

/// <summary>
///     Reads the object tree of an attached catalog for the UI's schema explorer.
/// </summary>
/// <remarks>
///     Introspection runs against <c>information_schema</c> in the attached catalog rather than
///     DuckLake's metadata tables directly, so the same code works for a DuckLake catalog, a plain
///     DuckDB file, or an attached external database.
/// </remarks>
public static class CatalogBrowser
{
    /// <summary>
    ///     Returns the schemas, tables, and columns visible in the session's current catalog.
    /// </summary>
    public static async Task<IReadOnlyList<SchemaInfo>> ReadSchemasAsync(
        Duckling duckling,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(duckling);

        // One query for the whole tree: per-table round trips make the explorer's cost scale with
        // catalog size, which is exactly wrong for the wide catalogs this product targets.
        const string Sql = """
            SELECT
                c.table_schema,
                c.table_name,
                COALESCE(t.table_type, 'BASE TABLE') AS table_type,
                c.column_name,
                c.data_type,
                c.is_nullable
            FROM information_schema.columns AS c
            LEFT JOIN information_schema.tables AS t
                ON  t.table_schema = c.table_schema
                AND t.table_name   = c.table_name
            WHERE c.table_schema NOT IN ('information_schema', 'pg_catalog')
              -- DuckLake surfaces its own metadata tables (ducklake_snapshot, ducklake_table, ...)
              -- through information_schema alongside user tables. Verified against DuckLake on
              -- DuckDB 1.5.3: ~20 internal tables per catalog. They are implementation detail and
              -- must not appear in the explorer.
              AND c.table_name NOT LIKE 'ducklake\_%' ESCAPE '\'
            ORDER BY c.table_schema, c.table_name, c.ordinal_position
            """;

        var result = await duckling.ExecuteQueryAsync(Sql, cancellationToken).ConfigureAwait(false);

        var schemas = new List<SchemaInfo>();
        var tablesBySchema = new Dictionary<string, List<TableInfo>>(StringComparer.Ordinal);
        var columnsByTable = new Dictionary<(string Schema, string Table), List<ColumnInfo>>();
        var tableKinds = new Dictionary<(string Schema, string Table), string>();
        var order = new List<(string Schema, string Table)>();

        foreach (var row in result.Rows)
        {
            var schema = Convert.ToString(row[0], CultureInfo.InvariantCulture) ?? string.Empty;
            var table = Convert.ToString(row[1], CultureInfo.InvariantCulture) ?? string.Empty;
            var kind = Convert.ToString(row[2], CultureInfo.InvariantCulture) ?? "BASE TABLE";
            var column = Convert.ToString(row[3], CultureInfo.InvariantCulture) ?? string.Empty;
            var dataType = Convert.ToString(row[4], CultureInfo.InvariantCulture) ?? string.Empty;
            var nullable = string.Equals(
                Convert.ToString(row[5], CultureInfo.InvariantCulture),
                "YES",
                StringComparison.OrdinalIgnoreCase);

            var key = (schema, table);
            if (!columnsByTable.TryGetValue(key, out var columns))
            {
                columns = [];
                columnsByTable[key] = columns;
                tableKinds[key] = kind;
                order.Add(key);
            }

            columns.Add(new ColumnInfo(column, dataType, nullable));
        }

        foreach (var key in order)
        {
            if (!tablesBySchema.TryGetValue(key.Schema, out var tables))
            {
                tables = [];
                tablesBySchema[key.Schema] = tables;
                schemas.Add(new SchemaInfo(key.Schema, tables));
            }

            tables.Add(new TableInfo(key.Table, tableKinds[key], columnsByTable[key]));
        }

        return schemas;
    }
}

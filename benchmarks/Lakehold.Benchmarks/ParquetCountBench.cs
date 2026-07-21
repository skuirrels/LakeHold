using System.Globalization;
using DuckDB.NET.Data;

namespace Lakehold.Benchmarks;

/// <summary>
///     Finding #4: read a backed-up table's row count from the Parquet footer instead of re-scanning
///     the source table with <c>count(*)</c>.
/// </summary>
/// <remarks>
///     Builds a source table, copies it to Parquet exactly as the backup does, then times the two
///     ways of learning its row count. The gap is a scan-vs-metadata gap, so it widens with row
///     count; a metadata catalog with very many files is where it matters, so the table is sized to
///     stand in for one.
/// </remarks>
public static class ParquetCountBench
{
    public static async Task RunAsync(long rowCount)
    {
        var dir = Directory.CreateTempSubdirectory("lakehold-bench-parquet");
        var parquet = Path.Combine(dir.FullName, "ducklake_data_file.parquet").Replace('\\', '/');

        try
        {
            await using var connection = new DuckDBConnection("Data Source=:memory:");
            await connection.OpenAsync().ConfigureAwait(false);

            // A metadata-table-ish shape: a few narrow columns, many rows.
            await ExecuteAsync(connection,
                "CREATE TABLE src AS " +
                $"SELECT i AS data_file_id, i % 1000 AS table_id, 'path/' || i AS path " +
                $"FROM range({rowCount.ToString(CultureInfo.InvariantCulture)}) t(i)").ConfigureAwait(false);

            await ExecuteAsync(connection,
                $"COPY (SELECT * FROM src) TO '{parquet}' (FORMAT PARQUET)").ConfigureAwait(false);

            var before = await Harness.MeasureAsync("before", 200,
                async () => await ScalarAsync(connection, "SELECT count(*) FROM src").ConfigureAwait(false),
                trials: 7).ConfigureAwait(false);

            var after = await Harness.MeasureAsync("after", 200,
                async () => await ScalarAsync(connection, $"SELECT num_rows FROM parquet_file_metadata('{parquet}')")
                    .ConfigureAwait(false),
                trials: 7).ConfigureAwait(false);

            Harness.PrintComparison(
                $"#4 Backup row count — {rowCount:N0}-row metadata table (real DuckDB {DuckDbVersion(connection)})",
                "ns/table",
                before,
                after);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    private static async Task ExecuteAsync(DuckDBConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static async Task<object?> ScalarAsync(DuckDBConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync().ConfigureAwait(false);
    }

    private static string DuckDbVersion(DuckDBConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT version()";
        return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture) ?? "?";
    }
}

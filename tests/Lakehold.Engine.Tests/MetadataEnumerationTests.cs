using Lakehold.Engine.Catalog;
using Lakehold.Engine.Configuration;
using Lakehold.Engine.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Lakehold.Engine.Tests;

/// <summary>
///     Cover for discovering the metadata tables to copy, which stopped being straightforward on
///     DuckDB 1.5.4: a local-file metadata catalog is still attached and readable, but is hidden from
///     <c>duckdb_databases()</c>, <c>duckdb_tables()</c>, <c>PRAGMA database_list</c>, and its own
///     <c>information_schema</c>. Enumeration therefore runs over an independent read-only connection.
/// </summary>
/// <remarks>
///     The regression these guard against was silent, which is the reason they exist: the export found
///     zero tables, wrote a manifest saying so, and reported success. A backup that restores nothing
///     while claiming to be complete is the exact failure invariants 12 and 16 are written against.
/// </remarks>
public sealed class MetadataEnumerationTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lakehold-meta-enum", Guid.NewGuid().ToString("N"));
    private CatalogDescriptor _catalog = null!;
    private DucklingPool _pool = null!;
    private LakehouseOptions _options = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _options = new LakehouseOptions
        {
            DataRoot = Path.Combine(_root, "data"),
            BackupRoot = Path.Combine(_root, "backups"),
        };

        _catalog = new CatalogDescriptor(
            "enumlake",
            CatalogMetadataKind.LocalFile,
            Path.Combine(_root, "enum.ducklake"),
            Path.Combine(_root, "data"));

        Directory.CreateDirectory(_catalog.DataPath);
        _pool = new DucklingPool(Options.Create(_options), NullLoggerFactory.Instance);

        var duckling = await Session();
        await Run(duckling, "CREATE TABLE t (id BIGINT)");
        await Run(duckling, "INSERT INTO t VALUES (1), (2), (3)");
        await Run(duckling, "DELETE FROM t WHERE id = 2");
    }

    public async Task DisposeAsync()
    {
        await _pool.DisposeAsync();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Temp cleanup failing must not fail the test run.
        }
    }

    [Fact]
    public async Task Export_finds_the_metadata_tables_even_when_the_catalog_is_hidden()
    {
        var duckling = await Session();

        // The precondition this test exists for: on DuckDB 1.5.4 the metadata catalog is invisible to
        // the session's own introspection. Asserting it keeps the test honest — if a later release
        // makes the catalog visible again, this stops silently testing nothing.
        var visible = await duckling.ExecuteQueryAsync(
            "SELECT count(*) FROM duckdb_tables() WHERE database_name LIKE '__ducklake_metadata_%'",
            CancellationToken.None);
        var visibleCount = Convert.ToInt64(visible.Rows[0][0], System.Globalization.CultureInfo.InvariantCulture);

        var destination = Path.Combine(_root, "export");
        Directory.CreateDirectory(destination);

        var exported = await MetadataExporter.ExportAsync(duckling, destination, CancellationToken.None);

        // Whatever the visibility, the export must find the real metadata table set.
        Assert.NotEmpty(exported);
        Assert.Contains(exported, t => t.Name == "ducklake_snapshot");
        Assert.Contains(exported, t => t.Name == "ducklake_table");

        // ducklake_delete_file is the one invariant 12 names explicitly: a backup missing it silently
        // reinstates deleted rows on restore, and this fixture deleted a row precisely so it is here.
        Assert.Contains(exported, t => t.Name == "ducklake_delete_file");

        // Every reported table produced a Parquet file.
        foreach (var table in exported)
        {
            Assert.True(
                File.Exists(Path.Combine(destination, $"{table.Name}.parquet")),
                $"{table.Name} was reported as exported but no Parquet file was written");
        }

        Assert.True(
            exported.Count > 10,
            $"expected the full DuckLake metadata table set, found {exported.Count} (visible to the session: {visibleCount})");
    }

    /// <summary>
    ///     Inlined data is the reason the table list cannot be hard-coded: DuckLake stages a small
    ///     commit in a per-table <c>ducklake_inlined_data_&lt;schema&gt;_&lt;table&gt;</c> whose name is
    ///     only knowable at run time. Those are committed rows that are not yet in Parquet, so an
    ///     export that skipped them would lose the newest writes while reporting success.
    /// </summary>
    [Fact]
    public async Task Export_includes_dynamically_named_inlined_data_tables()
    {
        var duckling = await Session();

        // Small enough to be inlined rather than written out as Parquet.
        await Run(duckling, "CREATE TABLE inline_me (id BIGINT)");
        await Run(duckling, "INSERT INTO inline_me VALUES (1)");

        var destination = Path.Combine(_root, "inlined-export");
        Directory.CreateDirectory(destination);

        var exported = await MetadataExporter.ExportAsync(duckling, destination, CancellationToken.None);

        Assert.Contains(exported, t => t.Name.StartsWith("ducklake_inlined_data_", StringComparison.Ordinal));
    }

    private Task<Duckling> Session()
        => _pool.GetOrStartAsync(_catalog, configure: null, CancellationToken.None);

    private static Task<QueryResult> Run(Duckling duckling, string sql)
        => duckling.ExecuteQueryAsync(sql, CancellationToken.None);
}

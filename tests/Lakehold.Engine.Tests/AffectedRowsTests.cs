using Lakehold.Engine.Catalog;
using Lakehold.Engine.Configuration;
using Lakehold.Engine.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Lakehold.Engine.Tests;

/// <summary>
///     Cover for DML reporting what it changed. The provider's dynamic API cannot supply this —
///     DuckDB.NET's reader reports <c>RecordsAffected == -1</c>, so a successful insert used to come
///     back indistinguishable from a statement that returned nothing, and both the workbench and the
///     wire endpoint reported zero.
/// </summary>
public sealed class AffectedRowsTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lakehold-tests", Guid.NewGuid().ToString("N"));
    private CatalogDescriptor _catalog = null!;
    private DucklingPool _pool = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);

        var options = new LakehouseOptions
        {
            DataRoot = Path.Combine(_root, "data"),
            BackupRoot = Path.Combine(_root, "backups"),
        };

        _catalog = new CatalogDescriptor(
            "dmllake",
            CatalogMetadataKind.LocalFile,
            Path.Combine(_root, "test.ducklake"),
            Path.Combine(_root, "data"));

        Directory.CreateDirectory(_catalog.DataPath);
        _pool = new DucklingPool(Options.Create(options), NullLoggerFactory.Instance);

        var duckling = await Session();
        await Run(duckling, "CREATE TABLE orders (id BIGINT, status VARCHAR)");
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
    public async Task Dml_reports_the_rows_it_changed()
    {
        var duckling = await Session();

        var inserted = await Run(duckling, "INSERT INTO orders VALUES (1, 'new'), (2, 'new'), (3, 'new')");
        Assert.Equal(3, inserted.RowsAffected);
        Assert.Empty(inserted.Columns);
        Assert.Empty(inserted.Rows);

        var updated = await Run(duckling, "UPDATE orders SET status = 'shipped' WHERE id < 3");
        Assert.Equal(2, updated.RowsAffected);

        var deleted = await Run(duckling, "DELETE FROM orders WHERE id = 3");
        Assert.Equal(1, deleted.RowsAffected);

        // A statement that matches nothing is a real zero, and stays distinguishable from a
        // statement that does not report a count at all.
        var missed = await Run(duckling, "DELETE FROM orders WHERE id = 99");
        Assert.Equal(0, missed.RowsAffected);

        var remaining = await Run(duckling, "SELECT count(*) FROM orders");
        Assert.Null(remaining.RowsAffected);
        Assert.Single(remaining.Rows);
    }

    [Fact]
    public async Task Merge_reports_the_rows_it_changed()
    {
        var duckling = await Session();
        await Run(duckling, "CREATE TABLE targets (id BIGINT, status VARCHAR)");
        await Run(duckling, "INSERT INTO targets VALUES (1, 'new'), (2, 'new')");

        var merged = await Run(
            duckling,
            """
            MERGE INTO targets AS t
            USING (SELECT 2 AS id, 'shipped' AS status UNION ALL SELECT 3, 'new') AS s
            ON t.id = s.id
            WHEN MATCHED THEN UPDATE SET status = s.status
            WHEN NOT MATCHED THEN INSERT VALUES (s.id, s.status)
            """);

        Assert.Equal(2, merged.RowsAffected);
    }

    /// <summary>
    ///     A DuckDB struct or map literal contains braces, which EF Core's raw-SQL path reads as
    ///     composite-format placeholders. The non-query path must not go through that formatting, so
    ///     this statement is the regression guard for it.
    /// </summary>
    [Fact]
    public async Task Braces_in_a_statement_reach_the_engine_intact()
    {
        var duckling = await Session();
        await Run(duckling, "CREATE TABLE payloads (id BIGINT, body STRUCT(a INTEGER, b VARCHAR), tags MAP(VARCHAR, INTEGER))");

        var inserted = await Run(
            duckling,
            "INSERT INTO payloads VALUES (1, {'a': 1, 'b': 'x'}, MAP {'k': 7})");

        Assert.Equal(1, inserted.RowsAffected);

        var read = await Run(duckling, "SELECT body.a, tags['k'] FROM payloads");
        var row = Assert.Single(read.Rows);
        Assert.Equal(1, Convert.ToInt32(row[0], System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(7, Convert.ToInt32(row[1], System.Globalization.CultureInfo.InvariantCulture));
    }

    private Task<Duckling> Session()
        => _pool.GetOrStartAsync(_catalog, configure: null, CancellationToken.None);

    private static Task<QueryResult> Run(Duckling duckling, string sql)
        => duckling.ExecuteQueryAsync(sql, CancellationToken.None);
}

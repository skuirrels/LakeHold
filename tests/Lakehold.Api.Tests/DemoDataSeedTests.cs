using System.Globalization;
using Lakehold.Engine.Catalog;
using Lakehold.Engine.Configuration;
using Lakehold.Engine.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Lakehold.Api.Tests;

/// <summary>
///     Cover for re-running the demo seed against a catalog that already holds it.
/// </summary>
/// <remarks>
///     The control-plane row and the catalog have separate lifetimes, so the seed can run again
///     against data it already wrote. An unguarded insert doubled every row of the public demo that
///     way, which no type check notices.
/// </remarks>
public sealed class DemoDataSeedTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lakehold-tests", Guid.NewGuid().ToString("N"));
    private DucklingPool _pool = null!;
    private CatalogDescriptor _catalog = null!;

    public Task InitializeAsync()
    {
        _catalog = new CatalogDescriptor(
            "analytics",
            CatalogMetadataKind.LocalFile,
            Path.Combine(_root, "analytics.ducklake"),
            Path.Combine(_root, "data"),
            TenantKey: "demo",
            CatalogId: 1);

        Directory.CreateDirectory(_catalog.DataPath);
        _pool = new DucklingPool(
            Options.Create(new LakehouseOptions { DataRoot = Path.Combine(_root, "data") }),
            NullLoggerFactory.Instance);

        return Task.CompletedTask;
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
    public async Task Seeding_an_already_seeded_catalog_adds_no_rows()
    {
        var duckling = await _pool.GetOrStartAsync(_catalog, configure: null, CancellationToken.None);

        await DemoData.SeedTablesAsync(duckling, CancellationToken.None);
        var snapshotsAfterFirst = await ScalarAsync(duckling, "SELECT count(*) FROM ducklake_snapshots('analytics')");

        await DemoData.SeedTablesAsync(duckling, CancellationToken.None);

        Assert.Equal(250_000, await ScalarAsync(duckling, "SELECT count(*) FROM events"));
        Assert.Equal(250_000, await ScalarAsync(duckling, "SELECT count(DISTINCT event_id) FROM events"));
        Assert.Equal(5_000, await ScalarAsync(duckling, "SELECT count(*) FROM customers"));

        // A guarded insert that matches nothing must not commit an empty snapshot either, or every
        // restart would grow the catalog's history with no-op entries.
        Assert.Equal(
            snapshotsAfterFirst,
            await ScalarAsync(duckling, "SELECT count(*) FROM ducklake_snapshots('analytics')"));
    }

    private static async Task<long> ScalarAsync(Duckling duckling, string sql)
    {
        var result = await duckling.ExecuteQueryAsync(sql, CancellationToken.None);
        return Convert.ToInt64(result.Rows[0][0], CultureInfo.InvariantCulture);
    }
}

using DuckDB.EFCoreProvider.Extensions;
using Lakehold.ControlPlane.Data;
using Lakehold.Engine.Catalog;
using Lakehold.Engine.Configuration;
using Lakehold.Engine.Execution;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Lakehold.Api.Tests;

/// <summary>
///     Cover for the catalog resolution cache and for the invalidation that keeps it honest.
/// </summary>
/// <remarks>
///     The cache removes a control-plane read from every query, which also means a stale entry is
///     never corrected by a later read. Nothing raises when that happens — the configuration change
///     just silently fails to take effect — so the invalidation path is worth pinning down.
/// </remarks>
public sealed class CatalogCacheTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lakehold-tests", Guid.NewGuid().ToString("N"));

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A temp directory we cannot remove is not worth failing a green test over.
        }

        return Task.CompletedTask;
    }

    private static ResolvedCatalog Resolution(string catalogName, string dataPath, int tenantId = 1)
        => new(
            new CatalogDescriptor(
                catalogName,
                CatalogMetadataKind.LocalFile,
                $"/tmp/{catalogName}.ducklake",
                dataPath),
            tenantId);

    [Fact]
    public void A_resolution_is_returned_to_the_tenant_that_stored_it()
    {
        var cache = new CatalogCache();
        cache.Set("demo", "analytics", Resolution("analytics", "/tmp/data/demo"));

        Assert.True(cache.TryGet("demo", "analytics", out var hit));
        Assert.Equal("/tmp/data/demo", hit.Descriptor.DataPath);
    }

    [Fact]
    public void A_resolution_never_leaks_to_another_tenant_or_catalog()
    {
        var cache = new CatalogCache();
        cache.Set("demo", "analytics", Resolution("analytics", "/tmp/data/demo"));

        // The composite key is what keeps the cache inside the isolation boundary: a hit must
        // require both halves to match, or one tenant's entry would answer another's query.
        Assert.False(cache.TryGet("other", "analytics", out _));
        Assert.False(cache.TryGet("demo", "warehouse", out _));
    }

    [Fact]
    public void Two_tenants_can_hold_a_catalog_of_the_same_name()
    {
        var cache = new CatalogCache();
        cache.Set("alpha", "analytics", Resolution("analytics", "/tmp/data/alpha", tenantId: 1));
        cache.Set("beta", "analytics", Resolution("analytics", "/tmp/data/beta", tenantId: 2));

        Assert.True(cache.TryGet("alpha", "analytics", out var alpha));
        Assert.True(cache.TryGet("beta", "analytics", out var beta));
        Assert.Equal("/tmp/data/alpha", alpha.Descriptor.DataPath);
        Assert.Equal("/tmp/data/beta", beta.Descriptor.DataPath);
    }

    [Fact]
    public void Invalidating_a_catalog_drops_it_for_every_tenant_that_holds_it()
    {
        var cache = new CatalogCache();
        cache.Set("alpha", "analytics", Resolution("analytics", "/tmp/data/alpha"));
        cache.Set("beta", "analytics", Resolution("analytics", "/tmp/data/beta"));
        cache.Set("alpha", "warehouse", Resolution("warehouse", "/tmp/data/alpha-wh"));

        cache.Invalidate("analytics");

        Assert.False(cache.TryGet("alpha", "analytics", out _));
        Assert.False(cache.TryGet("beta", "analytics", out _));

        // An unrelated catalog must survive: invalidation is scoped to the one that changed.
        Assert.True(cache.TryGet("alpha", "warehouse", out _));
    }

    [Fact]
    public async Task Forgetting_a_catalog_drops_the_cached_resolution()
    {
        var options = new LakehouseOptions();
        var cache = new CatalogCache();
        cache.Set("demo", "analytics", Resolution("analytics", "/tmp/data/demo"));

        await using var pool = new DucklingPool(Options.Create(options), NullLoggerFactory.Instance);
        var contextOptions = new DbContextOptionsBuilder<ControlPlaneContext>()
            .UseDuckDB($"Data Source={Path.Combine(_root, "controlplane.duckdb")}")
            .Options;

        await using var context = new ControlPlaneContext(contextOptions);
        await context.Database.EnsureCreatedAsync();

        var service = new LakehouseService(context, pool, cache, Options.Create(options));

        // The pool holds no warm session for this catalog, so eviction is a no-op and this asserts
        // the half that would otherwise be silently skipped.
        await service.ForgetCatalogAsync("analytics");

        Assert.False(cache.TryGet("demo", "analytics", out _));
    }
}

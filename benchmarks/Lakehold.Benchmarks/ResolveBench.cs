using System.Collections.Concurrent;
using DuckDB.EFCoreProvider.Extensions;
using Microsoft.EntityFrameworkCore;

namespace Lakehold.Benchmarks;

/// <summary>
///     Finding #1: resolve a tenant's catalog through a node-wide cache instead of a control-plane
///     read on every query.
/// </summary>
/// <remarks>
///     "before" is the <c>AsNoTracking</c> read with the tenant join that <c>LakehouseService</c> ran
///     per statement, against a real native-DuckDB control plane; "after" is the dictionary hit that
///     now serves the same value. A minimal stand-in model mirrors the real query shape so the suite
///     stays independent of the rest of the repo. The query is warmed first, so this measures the
///     round trip and not one-time EF query compilation.
/// </remarks>
public static class ResolveBench
{
    public static async Task RunAsync()
    {
        var file = Path.Combine(Path.GetTempPath(), $"lakehold-bench-cp-{Guid.NewGuid():N}.duckdb");

        try
        {
            var options = new DbContextOptionsBuilder<BenchControlPlane>()
                .UseDuckDB($"Data Source={file}")
                .Options;

            await using (var seed = new BenchControlPlane(options))
            {
                await seed.Database.EnsureCreatedAsync().ConfigureAwait(false);
                var tenant = new BenchTenant { Slug = "demo", DisplayName = "Demo" };
                seed.Tenants.Add(tenant);
                await seed.SaveChangesAsync().ConfigureAwait(false);
                seed.Catalogs.Add(new BenchCatalog
                {
                    TenantId = tenant.Id,
                    Name = "analytics",
                    DataPath = "/tmp/data/analytics",
                });
                await seed.SaveChangesAsync().ConfigureAwait(false);
            }

            await using var context = new BenchControlPlane(options);

            var before = await Harness.MeasureAsync("before", 500, async () =>
            {
                _ = await context.Catalogs
                    .AsNoTracking()
                    .Include(c => c.Tenant)
                    .FirstOrDefaultAsync(c => c.Tenant!.Slug == "demo" && c.Name == "analytics")
                    .ConfigureAwait(false);
            }, trials: 7).ConfigureAwait(false);

            // The cache hit that replaces the read above: the same composite-key lookup CatalogCache does.
            var cache = new ConcurrentDictionary<string, (string DataPath, int TenantId)>(StringComparer.Ordinal);
            cache["demo analytics"] = ("/tmp/data/analytics", 1);

            var after = await Harness.MeasureAsync("after", 500, () =>
            {
                cache.TryGetValue("demo analytics", out _);
                return Task.CompletedTask;
            }, trials: 7).ConfigureAwait(false);

            Harness.PrintComparison(
                "#1 Catalog resolve — per query (control-plane read vs cache hit)",
                "ns/resolve",
                before,
                after);
        }
        finally
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
    }
}

/// <summary>Minimal stand-in for the control-plane tenant record.</summary>
public sealed class BenchTenant
{
    public int Id { get; set; }
    public required string Slug { get; set; }
    public required string DisplayName { get; set; }
    public ICollection<BenchCatalog> Catalogs { get; } = [];
}

/// <summary>Minimal stand-in for the control-plane catalog record.</summary>
public sealed class BenchCatalog
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public required string Name { get; set; }
    public required string DataPath { get; set; }
    public BenchTenant? Tenant { get; set; }
}

/// <summary>Native-DuckDB EF context mirroring the real control plane's tenant→catalog shape.</summary>
public sealed class BenchControlPlane(DbContextOptions<BenchControlPlane> options) : DbContext(options)
{
    public DbSet<BenchTenant> Tenants => Set<BenchTenant>();
    public DbSet<BenchCatalog> Catalogs => Set<BenchCatalog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<BenchTenant>().HasIndex(t => t.Slug).IsUnique();
        modelBuilder.Entity<BenchCatalog>()
            .HasOne(c => c.Tenant)
            .WithMany(t => t.Catalogs)
            .HasForeignKey(c => c.TenantId);
    }
}

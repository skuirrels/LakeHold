using Microsoft.EntityFrameworkCore;
using Lakehold.ControlPlane.Data;
using Lakehold.ControlPlane.Model;
using Lakehold.Engine.Catalog;
using Lakehold.Engine.Execution;

namespace Lakehold.Api;

// CA1848/CA1873: source-generated logging exists to keep hot paths allocation-free. Seeding runs
// once at start-up, so the delegates would add ceremony for no measurable gain.
#pragma warning disable CA1848, CA1873

/// <summary>
///     Creates the control-plane schema and, on a first run, a demo tenant with a populated
///     catalog.
/// </summary>
/// <remarks>
///     A lakehouse with no data is not evaluable — the schema explorer is empty, every query fails,
///     and a reviewer cannot tell whether the product works. Seeding makes the first run
///     self-demonstrating.
/// </remarks>
internal static class DemoData
{
    private const string TenantSlug = "demo";
    private const string CatalogName = "analytics";

    public static async Task EnsureSeededAsync(IServiceProvider services, string stateRoot, ILogger logger)
    {
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<ControlPlaneContext>();

        await context.Database.EnsureCreatedAsync().ConfigureAwait(false);

        if (await context.Tenants.AnyAsync(t => t.Slug == TenantSlug).ConfigureAwait(false))
        {
            return;
        }

        var catalogRoot = Path.Combine(stateRoot, "catalogs");
        var dataRoot = Path.Combine(stateRoot, "data", CatalogName);
        Directory.CreateDirectory(catalogRoot);
        Directory.CreateDirectory(dataRoot);

        var tenant = new Tenant
        {
            Slug = TenantSlug,
            DisplayName = "Demo workspace",
            CreatedUtc = DateTimeOffset.UtcNow,
        };

        tenant.Catalogs.Add(new LakeCatalog
        {
            Name = CatalogName,
            MetadataKind = CatalogMetadataKind.LocalFile,
            MetadataSource = Path.Combine(catalogRoot, $"{CatalogName}.ducklake"),
            DataPath = dataRoot,
            CreatedUtc = DateTimeOffset.UtcNow,
        });

        context.Tenants.Add(tenant);
        await context.SaveChangesAsync().ConfigureAwait(false);

        await SeedCatalogAsync(scope.ServiceProvider, tenant.Catalogs.First(), logger).ConfigureAwait(false);
    }

    private static async Task SeedCatalogAsync(IServiceProvider services, LakeCatalog catalog, ILogger logger)
    {
        var pool = services.GetRequiredService<DucklingPool>();

        try
        {
            var duckling = await pool
                .GetOrStartAsync(catalog.ToDescriptor(), configure: null, CancellationToken.None)
                .ConfigureAwait(false);

            // Row counts are large enough that DuckLake writes real Parquet rather than inlining
            // the commit into the metadata catalog — which is what makes the "open format" claim
            // demonstrable rather than aspirational on a first run.
            string[] statements =
            [
                """
                CREATE TABLE IF NOT EXISTS events (
                    event_id    BIGINT,
                    occurred_at TIMESTAMP,
                    customer_id BIGINT,
                    event_type  VARCHAR,
                    revenue     DECIMAL(18,2),
                    country     VARCHAR
                )
                """,
                """
                INSERT INTO events
                SELECT
                    i                                                              AS event_id,
                    TIMESTAMP '2026-01-01 00:00:00' + INTERVAL (i % 20000) MINUTE  AS occurred_at,
                    (i % 5000) + 1                                                 AS customer_id,
                    -- Independent hashes per dimension. Plain modular arithmetic on a shared
                    -- counter correlates the dimensions whenever the moduli share a factor:
                    -- i % 4 and i % 6 both contain 2, which confines each country to two of the
                    -- four event types and makes every cross-tab in the demo look wrong.
                    ['view', 'signup', 'purchase', 'refund'][(CAST(hash(i * 2654435761) % 4 AS BIGINT)) + 1]  AS event_type,
                    ROUND((CAST(hash(i * 40503) % 50000 AS BIGINT)) / 100.0, 2)                               AS revenue,
                    ['GB', 'US', 'DE', 'FR', 'AU', 'JP'][(CAST(hash(i * 2246822519) % 6 AS BIGINT)) + 1]      AS country
                FROM range(250000) t(i)
                """,
                """
                CREATE TABLE IF NOT EXISTS customers (
                    customer_id BIGINT,
                    signed_up   DATE,
                    segment     VARCHAR,
                    country     VARCHAR
                )
                """,
                """
                INSERT INTO customers
                SELECT
                    i + 1                                            AS customer_id,
                    DATE '2024-01-01' + INTERVAL (i % 900) DAY       AS signed_up,
                    ['free', 'pro', 'enterprise'][(CAST(hash(i * 2654435761) % 3 AS BIGINT)) + 1]        AS segment,
                    ['GB', 'US', 'DE', 'FR', 'AU', 'JP'][(CAST(hash(i * 2246822519) % 6 AS BIGINT)) + 1] AS country
                FROM range(5000) t(i)
                """,
                """
                CREATE VIEW IF NOT EXISTS revenue_by_country AS
                SELECT
                    country,
                    count(*)                                    AS event_count,
                    ROUND(sum(revenue), 2)                      AS total_revenue,
                    ROUND(avg(revenue), 2)                      AS avg_revenue
                FROM events
                WHERE event_type = 'purchase'
                GROUP BY country
                ORDER BY total_revenue DESC
                """,
            ];

            foreach (var statement in statements)
            {
                await duckling.ExecuteQueryAsync(statement, CancellationToken.None).ConfigureAwait(false);
            }

            logger.LogInformation("Seeded demo catalog '{Catalog}' with 250k events and 5k customers", catalog.Name);
        }
        catch (Exception ex)
        {
            // Seeding is a convenience. A failure here must not stop the API from starting —
            // the operator can still create their own catalogs.
            logger.LogWarning(ex, "Could not seed the demo catalog. The API will start without sample data.");
        }
    }
}
#pragma warning restore CA1848, CA1873

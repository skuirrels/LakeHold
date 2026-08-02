using DuckDB.EFCoreProvider.Extensions;
using Lakehold.Api.Endpoints;
using Lakehold.ControlPlane.Data;
using Lakehold.ControlPlane.Model;
using Lakehold.Engine.Catalog;
using Lakehold.Engine.Configuration;
using Lakehold.Engine.Execution;
using Lakehold.Linq.Compiler;
using Lakehold.Querying;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Lakehold.Api.Tests;

/// <summary>
///     End-to-end control/data-plane coverage for saved queries: catalog isolation, optimistic
///     authoring, structurally read-only execution, and the explicit view lifecycle.
/// </summary>
public sealed class SavedQueryServiceTests : IAsyncLifetime
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "lakehold-saved-queries", Guid.NewGuid().ToString("N"));

    private ControlPlaneContext _context = null!;
    private DucklingPool _pool = null!;
    private LakehouseService _lakehouse = null!;
    private SavedQueryService _savedQueries = null!;
    private QuerySourcePlanningService _planning = null!;
    private IOptions<LakehouseOptions> _options = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _options = Options.Create(new LakehouseOptions
        {
            MetadataRoot = Path.Combine(_root, "catalogs"),
            DataRoot = Path.Combine(_root, "data"),
        });

        var builder = new DbContextOptionsBuilder<ControlPlaneContext>();
        builder.UseDuckDB($"Data Source={Path.Combine(_root, "controlplane.duckdb")}");
        _context = new ControlPlaneContext(builder.Options);
        await _context.Database.EnsureCreatedAsync();

        _pool = new DucklingPool(_options, NullLoggerFactory.Instance);
        _lakehouse = new LakehouseService(_context, _pool, _options);
        var compiler = new LinqQueryCompiler(
            Options.Create(new LinqCompilerOptions()));
        _planning = new QuerySourcePlanningService(_lakehouse, new InProcessPlanner(compiler));
        _savedQueries = new SavedQueryService(_context, _lakehouse, TimeProvider.System, _planning);

        await AdminEndpoints.CreateTenantAsync(
            new CreateTenantRequest("acme", "Acme"), _context, TimeProvider.System, default);
        await CreateLegacyLocalCatalogAsync("analytics");
        await CreateLegacyLocalCatalogAsync("finance");

        await Sql("CREATE TABLE events (country VARCHAR, revenue DECIMAL(18, 2))");
        await Sql("INSERT INTO events VALUES ('GB', 10.00), ('GB', 15.00), ('US', 7.00)");
    }

    public async Task DisposeAsync()
    {
        await _pool.DisposeAsync();
        await _context.DisposeAsync();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Temp cleanup failing must not fail the run.
        }
    }

    [Fact]
    public async Task Query_can_be_authored_revised_and_executed_read_only()
    {
        var created = await _savedQueries.CreateAsync(
            "acme",
            "analytics",
            "Revenue by country",
            "One row per country.",
            "SELECT country, sum(revenue) AS total FROM events GROUP BY country ORDER BY country",
            tokenId: 12,
            default);

        Assert.Equal(1, created.Revision);
        Assert.Equal(1, created.ConcurrencyVersion);
        Assert.Equal(12, created.CreatedByTokenId);

        var result = await _savedQueries.ExecuteAsync(
            "acme", "analytics", created.Id, tokenId: 12, recordHistory: true, default);
        Assert.Equal(2, result.Rows.Count);
        Assert.Equal("GB", result.Rows[0][0]);

        var updated = await _savedQueries.UpdateAsync(
            "acme",
            "analytics",
            created.Id,
            expectedRevision: 1,
            "UK revenue",
            null,
            "SELECT sum(revenue) AS total FROM events WHERE country = 'GB'",
            tokenId: 13,
            default);

        Assert.Equal(2, updated.Revision);
        Assert.Equal(2, updated.ConcurrencyVersion);
        Assert.Equal(13, updated.UpdatedByTokenId);
        Assert.Single(await _savedQueries.ListAsync("acme", "analytics", default));

        await Assert.ThrowsAsync<SavedQueryConflictException>(() => _savedQueries.UpdateAsync(
            "acme",
            "analytics",
            created.Id,
            expectedRevision: 1,
            "Stale edit",
            null,
            "SELECT 1",
            tokenId: null,
            default));
    }

    [Fact]
    public async Task Linq_definition_is_translated_executed_and_recorded_as_authored_source()
    {
        const string source = """
            (from e in Main.Events
             where e.Country == "GB"
             orderby e.Revenue descending
             select new { e.Country, e.Revenue }).Take(2)
            """;
        var query = await _savedQueries.CreateAsync(
            "acme",
            "analytics",
            "UK revenue in LINQ",
            null,
            source,
            "csharp-linq",
            tokenId: 44,
            default);

        var execution = await _savedQueries.ExecutePlannedAsync(
            "acme", "analytics", query.Id, tokenId: 44, recordHistory: true, default);

        Assert.Equal("csharp-linq", query.Language);
        Assert.Contains("SELECT", execution.Plan.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("LIMIT $", execution.Plan.Sql, StringComparison.Ordinal);
        Assert.Single(execution.Plan.Parameters);
        Assert.Equal(2, execution.Result.Rows.Count);
        var run = await _context.QueryRuns.OrderByDescending(candidate => candidate.Id).FirstAsync();
        Assert.Equal("csharp-linq", run.Language);
        Assert.Equal(source, run.Sql);
    }

    [Fact]
    public async Task Published_view_tracks_the_revision_and_has_an_explicit_lifecycle()
    {
        var query = await _savedQueries.CreateAsync(
            "acme",
            "analytics",
            "Revenue total",
            null,
            "SELECT sum(revenue) AS total FROM events",
            tokenId: null,
            default);

        var published = await _savedQueries.PublishAsync(
            "acme", "analytics", query.Id, query.Revision, "main", "revenue_total", null, default);
        Assert.Equal(query.Revision, published.PublishedRevision);
        Assert.Equal(2, published.ConcurrencyVersion);
        Assert.Equal("revenue_total", published.PublishedViewName);
        Assert.Equal(32m, Convert.ToDecimal((await Sql("SELECT total FROM revenue_total")).Rows[0][0]));

        var revised = await _savedQueries.UpdateAsync(
            "acme",
            "analytics",
            query.Id,
            query.Revision,
            query.Name,
            null,
            "SELECT sum(revenue) AS total FROM events WHERE country = 'GB'",
            null,
            default);
        Assert.True(revised.PublishedRevision < revised.Revision);

        var republished = await _savedQueries.PublishAsync(
            "acme", "analytics", query.Id, revised.Revision, "main", "revenue_total", null, default);
        Assert.Equal(republished.Revision, republished.PublishedRevision);
        Assert.Equal(25m, Convert.ToDecimal((await Sql("SELECT total FROM revenue_total")).Rows[0][0]));

        await Assert.ThrowsAsync<SavedQueryConflictException>(() => _savedQueries.DeleteAsync(
            "acme", "analytics", query.Id, republished.Revision, default));

        var unpublished = await _savedQueries.UnpublishAsync(
            "acme", "analytics", query.Id, republished.Revision, null, default);
        Assert.Null(unpublished.PublishedViewName);
        Assert.Equal(5, unpublished.ConcurrencyVersion);

        await _savedQueries.DeleteAsync("acme", "analytics", query.Id, unpublished.Revision, default);
        Assert.Empty(await _savedQueries.ListAsync("acme", "analytics", default));
        await Assert.ThrowsAsync<DuckDB.NET.Data.DuckDBException>(
            () => Sql("SELECT * FROM revenue_total"));
    }

    [Fact]
    public async Task Catalog_boundary_and_single_read_statement_are_enforced()
    {
        await Assert.ThrowsAsync<SavedQueryValidationException>(() => _savedQueries.CreateAsync(
            "acme", "analytics", "DDL", null, "DROP TABLE events", null, default));

        await Assert.ThrowsAsync<SavedQueryValidationException>(() => _savedQueries.CreateAsync(
            "acme", "analytics", "Two", null, "SELECT 1; SELECT 2", null, default));

        await Assert.ThrowsAsync<SavedQueryValidationException>(() => _savedQueries.CreateAsync(
            "acme",
            "analytics",
            "Hidden DML",
            null,
            "WITH doomed AS (SELECT 'US') DELETE FROM events WHERE country IN (SELECT * FROM doomed)",
            null,
            default));

        var query = await _savedQueries.CreateAsync(
            "acme", "analytics", "One", null, "SELECT ';' AS value", null, default);

        await Assert.ThrowsAsync<SavedQueryNotFoundException>(
            () => _savedQueries.GetAsync("acme", "finance", query.Id, default));
        Assert.Empty(await _savedQueries.ListAsync("acme", "finance", default));
    }

    [Fact]
    public async Task External_plan_contract_rejects_schema_mismatch_and_external_access()
    {
        var schema = await _planning.GetCatalogSchemaAsync("acme", "analytics", default);
        var mismatched = new QuerySourcePlanningService(
            _lakehouse,
            new FixedPlanner(new QueryPlan("SELECT 1", [], [], "different-schema")));
        await Assert.ThrowsAsync<QueryPlanRejectedException>(() => mismatched.PlanAsync(
            "acme", "analytics", "csharp-linq", "Main.Events", default));

        var external = new QuerySourcePlanningService(
            _lakehouse,
            new FixedPlanner(new QueryPlan(
                "SELECT * FROM read_parquet('s3://outside/secret.parquet')",
                [],
                [],
                schema.SchemaFingerprint)));
        await Assert.ThrowsAsync<QueryPlanRejectedException>(() => external.PlanAsync(
            "acme", "analytics", "csharp-linq", "Main.Events", default));

        var mismatchedParameters = new QuerySourcePlanningService(
            _lakehouse,
            new FixedPlanner(new QueryPlan("SELECT $missing", [], [], schema.SchemaFingerprint)));
        await Assert.ThrowsAsync<QueryPlanRejectedException>(() => mismatchedParameters.PlanAsync(
            "acme", "analytics", "csharp-linq", "Main.Events", default));
    }

    [Fact]
    public async Task Plan_cache_is_reused_but_schema_drift_forces_recompilation()
    {
        var compiler = new LinqQueryCompiler(Options.Create(new LinqCompilerOptions()));
        var planner = new CountingPlanner(compiler);
        var planning = new QuerySourcePlanningService(
            _lakehouse,
            planner,
            cache: new QueryPlanCache());
        const string source = "Main.Events.Where(e => e.Country == \"GB\")";

        _ = await planning.PlanAsync("acme", "analytics", "csharp-linq", source, default);
        _ = await planning.PlanAsync("acme", "analytics", "csharp-linq", source, default);
        Assert.Equal(1, planner.PlanCalls);

        await Sql("ALTER TABLE events ADD COLUMN cache_buster INTEGER");
        _ = await planning.PlanAsync("acme", "analytics", "csharp-linq", source, default);
        Assert.Equal(2, planner.PlanCalls);
    }

    [Fact]
    public async Task Query_names_are_unique_within_a_catalog_not_across_the_tenant()
    {
        var analytics = await _savedQueries.CreateAsync(
            "acme", "analytics", "Daily revenue", null, "SELECT 1", null, default);
        var finance = await _savedQueries.CreateAsync(
            "acme", "finance", "Daily revenue", null, "SELECT 2", null, default);

        Assert.NotEqual(analytics.Id, finance.Id);
        Assert.Single(await _savedQueries.ListAsync("acme", "analytics", default));
        Assert.Single(await _savedQueries.ListAsync("acme", "finance", default));

        await Assert.ThrowsAsync<SavedQueryConflictException>(() => _savedQueries.CreateAsync(
            "acme", "finance", "Daily revenue", null, "SELECT 3", null, default));
    }

    [Fact]
    public async Task First_publish_removes_its_view_when_metadata_finalisation_fails()
    {
        var query = await _savedQueries.CreateAsync(
            "acme", "analytics", "Recoverable publish", null, "SELECT 42 AS answer", null, default);
        var invalidated = false;
        var clock = new CallbackTimeProvider(() =>
        {
            if (!invalidated)
            {
                invalidated = true;
                _context.Entry(query).Property(q => q.ConcurrencyVersion).OriginalValue = int.MaxValue;
            }

            return DateTimeOffset.UtcNow;
        });
        var service = new SavedQueryService(_context, _lakehouse, clock, _planning);

        await Assert.ThrowsAsync<SavedQueryConflictException>(() => service.PublishAsync(
            "acme", "analytics", query.Id, query.Revision, "main", "recoverable_publish", null, default));

        await Assert.ThrowsAsync<DuckDB.NET.Data.DuckDBException>(
            () => Sql("SELECT * FROM recoverable_publish"));

        var reloaded = await _savedQueries.GetAsync("acme", "analytics", query.Id, default);
        Assert.Null(reloaded.PublishedViewName);

        var published = await _savedQueries.PublishAsync(
            "acme",
            "analytics",
            query.Id,
            reloaded.Revision,
            "main",
            "recoverable_publish",
            null,
            default);
        Assert.Equal("recoverable_publish", published.PublishedViewName);
        Assert.Equal(42, Convert.ToInt32((await Sql("SELECT answer FROM recoverable_publish")).Rows[0][0]));
    }

    private Task<QueryResult> Sql(string sql)
        => _lakehouse.ExecuteAsync("acme", "analytics", sql, default);

    private async Task CreateLegacyLocalCatalogAsync(string name)
    {
        var tenantId = await _context.Tenants
            .Where(tenant => tenant.Slug == "acme")
            .Select(tenant => tenant.Id)
            .SingleAsync();
        var metadataRoot = _options.Value.MetadataRoot;
        var dataPath = Path.Combine(_options.Value.DataRoot, "acme", name);
        Directory.CreateDirectory(metadataRoot);
        Directory.CreateDirectory(dataPath);

        _context.Catalogs.Add(new LakeCatalog
        {
            TenantId = tenantId,
            Name = name,
            MetadataKind = CatalogMetadataKind.LocalFile,
            MetadataSource = Path.Combine(metadataRoot, $"{name}.ducklake"),
            DataPath = dataPath,
            StorageKind = ParquetStorageKind.Local,
            CreatedUtc = DateTimeOffset.UtcNow,
        });
        await _context.SaveChangesAsync();
    }

    private sealed class CallbackTimeProvider(Func<DateTimeOffset> utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow();
    }

    private sealed class InProcessPlanner(LinqQueryCompiler compiler) : IQuerySourcePlanner
    {
        public Task<IReadOnlyList<QueryLanguageDescriptor>> GetLanguagesAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<QueryLanguageDescriptor>>([]);

        public Task<QueryLanguageStarter> CreateStarterAsync(
            string language,
            QueryCatalogSchema catalogSchema,
            CancellationToken cancellationToken)
            => Task.FromResult(string.Equals(language, "sql", StringComparison.Ordinal)
                ? new QueryLanguageStarter("SELECT 1", catalogSchema.SchemaFingerprint)
                : compiler.CreateStarter(catalogSchema));

        public Task<QueryPlan> PlanAsync(
            string language,
            QueryPlanningRequest planningRequest,
            CancellationToken cancellationToken)
            => string.Equals(language, "sql", StringComparison.Ordinal)
                ? Task.FromResult(new QueryPlan(
                    planningRequest.Source,
                    [],
                    [],
                    planningRequest.SchemaFingerprint))
                : compiler.CompileAsync(planningRequest, cancellationToken);
    }

    private sealed class FixedPlanner(QueryPlan plan) : IQuerySourcePlanner
    {
        public Task<IReadOnlyList<QueryLanguageDescriptor>> GetLanguagesAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<QueryLanguageDescriptor>>([]);

        public Task<QueryLanguageStarter> CreateStarterAsync(
            string language,
            QueryCatalogSchema catalogSchema,
            CancellationToken cancellationToken)
            => Task.FromResult(new QueryLanguageStarter("Main.Events", catalogSchema.SchemaFingerprint));

        public Task<QueryPlan> PlanAsync(
            string language,
            QueryPlanningRequest planningRequest,
            CancellationToken cancellationToken)
            => Task.FromResult(plan);
    }

    private sealed class CountingPlanner(LinqQueryCompiler compiler) : IQuerySourcePlanner
    {
        public int PlanCalls { get; private set; }

        public Task<IReadOnlyList<QueryLanguageDescriptor>> GetLanguagesAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<QueryLanguageDescriptor>>([]);

        public Task<QueryLanguageStarter> CreateStarterAsync(
            string language,
            QueryCatalogSchema catalogSchema,
            CancellationToken cancellationToken)
            => Task.FromResult(compiler.CreateStarter(catalogSchema));

        public Task<QueryPlan> PlanAsync(
            string language,
            QueryPlanningRequest planningRequest,
            CancellationToken cancellationToken)
        {
            PlanCalls++;
            return compiler.CompileAsync(planningRequest, cancellationToken);
        }
    }
}

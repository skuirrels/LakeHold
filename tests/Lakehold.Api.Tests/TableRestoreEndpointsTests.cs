using DuckDB.EFCoreProvider.Extensions;
using Lakehold.Api.Endpoints;
using Lakehold.ControlPlane.Data;
using Lakehold.Engine.Catalog;
using Lakehold.Engine.Configuration;
using Lakehold.Engine.Execution;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Lakehold.Api.Tests;

/// <summary>Verifies the table-restore DTO boundary against a real DuckLake catalog.</summary>
public sealed class TableRestoreEndpointsTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "lakehold-restore-api", Guid.NewGuid().ToString("N"));
    private ControlPlaneContext _context = null!;
    private DucklingPool _pool = null!;
    private LakehouseService _service = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        var options = Options.Create(new LakehouseOptions
        {
            MetadataRoot = Path.Combine(_root, "catalogs"),
            DataRoot = Path.Combine(_root, "data"),
        });

        var builder = new DbContextOptionsBuilder<ControlPlaneContext>();
        builder.UseDuckDB($"Data Source={Path.Combine(_root, "cp.duckdb")}");
        _context = new ControlPlaneContext(builder.Options);
        await _context.Database.EnsureCreatedAsync();

        _pool = new DucklingPool(options, NullLoggerFactory.Instance);
        _service = new LakehouseService(_context, _pool, options);
        await AdminEndpoints.CreateTenantAsync(
            new CreateTenantRequest("acme", "Acme"), _context, TimeProvider.System, default);
        await AdminEndpoints.CreateCatalogAsync(
            "acme", new CreateCatalogRequest("analytics"), _context, options, TimeProvider.System, default);
        var catalog = await _context.Catalogs.SingleAsync();
        Directory.CreateDirectory(options.Value.MetadataRoot);
        catalog.MetadataKind = CatalogMetadataKind.LocalFile;
        catalog.MetadataSource = Path.Combine(options.Value.MetadataRoot, "analytics.ducklake");
        catalog.MetadataSchema = null;
        catalog.MetadataSecretName = null;
        await _context.SaveChangesAsync();
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
            // Temp cleanup failing must not fail the test run.
        }
    }

    [Fact]
    public async Task Endpoint_plans_then_applies_an_awkwardly_named_table_restore()
    {
        await Sql("""CREATE TABLE "order.items" (id BIGINT NOT NULL, status VARCHAR DEFAULT 'new')""");
        await Sql("""INSERT INTO "order.items"(id) VALUES (1)""");
        var snapshot = (await _service.GetSnapshotsAsync("acme", "analytics", 1, default)).Single().SnapshotId;
        await Sql("""INSERT INTO "order.items" VALUES (2, 'later')""");

        var planResult = await LakehouseEndpoints.RestoreTableAsync(
            "acme",
            "analytics",
            snapshot,
            new RestoreTableRequest("order.items"),
            _service,
            default);
        var plan = Assert.IsType<Ok<TableRestoreDto>>(Unwrap(planResult)).Value!;

        Assert.True(plan.DryRun);
        Assert.Equal(2, plan.CurrentRowCount);
        Assert.Equal(1, plan.HistoricalRowCount);
        Assert.Equal(["id", "status"], plan.RestoredColumns);
        Assert.Equal(2, await Count());

        var applyResult = await LakehouseEndpoints.RestoreTableAsync(
            "acme",
            "analytics",
            snapshot,
            new RestoreTableRequest(
                "order.items",
                Apply: true,
                ExpectedCurrentSnapshotId: plan.CurrentSnapshotId),
            _service,
            default);
        var applied = Assert.IsType<Ok<TableRestoreDto>>(Unwrap(applyResult)).Value!;

        Assert.False(applied.DryRun);
        Assert.Equal(1, await Count());
        await Sql("""INSERT INTO "order.items"(id) VALUES (3)""");
        var defaulted = await Sql("""SELECT status FROM "order.items" WHERE id = 3""");
        Assert.Equal("new", Convert.ToString(defaulted.Rows.Single()[0]));
    }

    private Task<QueryResult> Sql(string sql)
        => _service.ExecuteAsync("acme", "analytics", sql, default);

    private async Task<long> Count()
    {
        var result = await Sql("SELECT count(*) FROM \"order.items\"");
        return Convert.ToInt64(result.Rows.Single()[0], System.Globalization.CultureInfo.InvariantCulture);
    }

    private static IResult Unwrap(object union) => ((INestedHttpResult)union).Result;
}

using DuckDB.EFCoreProvider.Extensions;
using Lakehold.ControlPlane.Data;
using Lakehold.ControlPlane.Model;
using Lakehold.ControlPlane.Security;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Lakehold.Api.Tests;

/// <summary>
///     Cover for the additive schema upgrade: a control-plane database created before an entity
///     existed must gain that entity's table at start-up, without touching anything already there.
/// </summary>
public sealed class AdditiveSchemaTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lakehold-tests", Guid.NewGuid().ToString("N"));
    private string _dbPath = null!;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _dbPath = Path.Combine(_root, "controlplane.duckdb");
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
            // Temp cleanup failing must not fail the test run.
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task Missing_model_table_is_created_and_existing_rows_survive()
    {
        // A database from "before the feature": full schema, then the subscriptions table dropped to
        // simulate a deployment initialised by an older build, with user state already in it.
        //
        // The sequence is dropped too, and that detail is the entire point. An earlier version of
        // this test dropped only the table, leaving the sequence behind — so the create script's
        // DEFAULT nextval() resolved, the test passed, and the real upgrade still died at start-up
        // with "Sequence ChangeSubscriptions_Id_seq does not exist". A fixture that is easier to
        // satisfy than production is worse than no fixture, because it reports success.
        await using (var context = NewContext())
        {
            await context.Database.EnsureCreatedAsync();
            context.Tenants.Add(new Tenant { Slug = "acme", DisplayName = "Acme", CreatedUtc = DateTimeOffset.UtcNow });
            await context.SaveChangesAsync();
            await context.Database.ExecuteSqlRawAsync("DROP TABLE \"ChangeSubscriptions\"");
            await context.Database.ExecuteSqlRawAsync("DROP SEQUENCE IF EXISTS \"ChangeSubscriptions_Id_seq\"");
        }

        await using (var context = NewContext())
        {
            var created = await AdditiveSchema.EnsureModelTablesAsync(context, CancellationToken.None);
            Assert.Equal(1, created);

            // Pre-existing state is intact.
            Assert.Equal(1, await context.Tenants.CountAsync());

            // Writable through the model, not merely queryable: an insert exercises the
            // auto-increment sequence, so a create script that missed the sequence fails here
            // rather than on a deployment's first subscription.
            var tenant = await context.Tenants.SingleAsync();
            context.ChangeSubscriptions.Add(new ChangeSubscription
            {
                TenantId = tenant.Id,
                CatalogName = "analytics",
                EndpointUrl = "https://example.test/hook",
                Secret = "a-signing-secret-of-adequate-length",
                CreatedUtc = DateTimeOffset.UtcNow,
            });
            await context.SaveChangesAsync();

            var subscription = await context.ChangeSubscriptions.SingleAsync();
            Assert.True(subscription.Id > 0, "the recreated table's auto-increment must produce keys");
        }
    }

    [Fact]
    public async Task Missing_api_tokens_table_is_created_and_usable()
    {
        // A database initialised before tokens existed: full schema, then the ApiTokens table and its
        // sequence dropped, with a tenant already present.
        await using (var context = NewContext())
        {
            await context.Database.EnsureCreatedAsync();
            context.Tenants.Add(new Tenant { Slug = "acme", DisplayName = "Acme", CreatedUtc = DateTimeOffset.UtcNow });
            await context.SaveChangesAsync();
            await context.Database.ExecuteSqlRawAsync("DROP TABLE \"ApiTokens\"");
            await context.Database.ExecuteSqlRawAsync("DROP SEQUENCE IF EXISTS \"ApiTokens_Id_seq\"");
        }

        await using (var context = NewContext())
        {
            var created = await AdditiveSchema.EnsureModelTablesAsync(context, CancellationToken.None);
            Assert.Equal(1, created);
            Assert.Equal(1, await context.Tenants.CountAsync());

            // A tenant token (FK set) and an instance token (FK null) both insert — exercising the
            // recreated auto-increment sequence and the nullable tenant relationship.
            var tenant = await context.Tenants.SingleAsync();
            context.ApiTokens.Add(ApiTokenFactory.Issue(TokenScope.Tenant, tenant, "bi", DateTimeOffset.UtcNow).Record);
            context.ApiTokens.Add(ApiTokenFactory.Issue(TokenScope.Instance, null, "admin", DateTimeOffset.UtcNow).Record);
            await context.SaveChangesAsync();

            Assert.Equal(2, await context.ApiTokens.CountAsync());
            Assert.True(await context.ApiTokens.AllAsync(t => t.Id > 0), "the recreated table's auto-increment must produce keys");
        }
    }

    [Fact]
    public async Task A_complete_database_is_left_untouched()
    {
        await using var context = NewContext();
        await context.Database.EnsureCreatedAsync();

        Assert.Equal(0, await AdditiveSchema.EnsureModelTablesAsync(context, CancellationToken.None));
    }

    private ControlPlaneContext NewContext()
    {
        var builder = new DbContextOptionsBuilder<ControlPlaneContext>();
        builder.UseDuckDB($"Data Source={_dbPath}");
        return new ControlPlaneContext(builder.Options);
    }
}

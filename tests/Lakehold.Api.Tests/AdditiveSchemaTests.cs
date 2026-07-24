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
        Assert.Equal(0, await AdditiveSchema.EnsureModelColumnsAsync(context, CancellationToken.None));
    }

    [Fact]
    public async Task Missing_column_is_added_to_an_existing_table_and_usable()
    {
        // A database from before QueryRun.TokenId existed: full schema, then the column dropped, with
        // a tenant and an audit row already present. DuckDB refuses to alter a table while an index
        // references it, so the index is dropped for the removal and recreated afterwards — which also
        // reproduces the production shape exactly: the column is added back with the index in place.
        int tenantId;
        await using (var context = NewContext())
        {
            await context.Database.EnsureCreatedAsync();
            var tenant = new Tenant { Slug = "acme", DisplayName = "Acme", CreatedUtc = DateTimeOffset.UtcNow };
            context.Tenants.Add(tenant);
            await context.SaveChangesAsync();
            tenantId = tenant.Id;

            await context.Database.ExecuteSqlAsync(
                $"INSERT INTO \"QueryRuns\" (\"TenantId\", \"CatalogName\", \"Sql\", \"StartedUtc\", \"ElapsedMilliseconds\", \"RowCount\", \"Succeeded\") VALUES ({tenantId}, 'analytics', 'SELECT 1', now(), 1.0, 1, true)");

            foreach (var index in await IndexNamesAsync(context, "QueryRuns"))
            {
                // An index name cannot be parameterised; these come from the database's own catalog.
                await ExecuteDdlAsync(context, "DROP INDEX \"" + index + "\"");
            }

            await context.Database.ExecuteSqlRawAsync("ALTER TABLE \"QueryRuns\" DROP COLUMN \"TokenId\"");

            // Put an index back so the additive pass adds the column to an indexed table, as it does
            // in production.
            await context.Database.ExecuteSqlRawAsync(
                "CREATE INDEX \"IX_QueryRuns_TenantId_StartedUtc\" ON \"QueryRuns\" (\"TenantId\", \"StartedUtc\")");
        }

        await using (var context = NewContext())
        {
            var added = await AdditiveSchema.EnsureModelColumnsAsync(context, CancellationToken.None);
            Assert.Equal(1, added);

            // The pre-existing audit row survives, with the new column defaulting to null.
            var existing = await context.QueryRuns.SingleAsync();
            Assert.Null(existing.TokenId);

            // And the column is writable through the model.
            context.QueryRuns.Add(new QueryRun
            {
                TenantId = tenantId,
                CatalogName = "analytics",
                Sql = "SELECT 2",
                StartedUtc = DateTimeOffset.UtcNow,
                TokenId = 42,
            });
            await context.SaveChangesAsync();

            Assert.Equal(42, (await context.QueryRuns.OrderByDescending(r => r.Id).FirstAsync()).TokenId);

            // Idempotent: a second pass adds nothing.
            Assert.Equal(0, await AdditiveSchema.EnsureModelColumnsAsync(context, CancellationToken.None));
        }
    }

    /// <summary>Runs fixture DDL whose identifiers cannot be parameterised, off EF's raw-SQL path.</summary>
    private static async Task ExecuteDdlAsync(ControlPlaneContext context, string sql)
    {
        var connection = context.Database.GetDbConnection();
        var opened = connection.State != System.Data.ConnectionState.Open;
        if (opened)
        {
            await connection.OpenAsync();
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }
        finally
        {
            if (opened)
            {
                await connection.CloseAsync();
            }
        }
    }

    /// <summary>Index names on a table, so a fixture can drop what blocks an ALTER and restore it.</summary>
    private static async Task<List<string>> IndexNamesAsync(ControlPlaneContext context, string table)
    {
        var names = new List<string>();
        var connection = context.Database.GetDbConnection();
        var opened = connection.State != System.Data.ConnectionState.Open;
        if (opened)
        {
            await connection.OpenAsync();
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT index_name FROM duckdb_indexes() WHERE table_name = $table";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "table";
            parameter.Value = table;
            command.Parameters.Add(parameter);

            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                if (reader.GetValue(0) is string name)
                {
                    names.Add(name);
                }
            }
        }
        finally
        {
            if (opened)
            {
                await connection.CloseAsync();
            }
        }

        return names;
    }

    private ControlPlaneContext NewContext()
    {
        var builder = new DbContextOptionsBuilder<ControlPlaneContext>();
        builder.UseDuckDB($"Data Source={_dbPath}");
        return new ControlPlaneContext(builder.Options);
    }
}

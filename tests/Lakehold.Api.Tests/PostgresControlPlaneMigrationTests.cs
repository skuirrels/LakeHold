using Lakehold.Api.Storage;
using Lakehold.ControlPlane.Data;
using Lakehold.ControlPlane.Model;
using Lakehold.Engine.Catalog;
using Lakehold.Engine.Configuration;
using Lakehold.Engine.Execution;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace Lakehold.Api.Tests;

public sealed class PostgresControlPlaneMigrationTests
{
    [Fact]
    public void Browser_authentication_key_ring_migration_is_discoverable()
    {
        var options = new DbContextOptionsBuilder<ControlPlaneContext>()
            .UseNpgsql("Host=localhost;Database=not-opened;Username=unused;Password=unused")
            .Options;
        using var context = new ControlPlaneContext(options);

        Assert.Contains(
            "20260729194500_AddBrowserAuthentication",
            context.Database.GetMigrations());
    }

    [Fact]
    public async Task Object_storage_credentials_are_scoped_to_the_tenant_catalog_prefixes()
    {
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var root = Path.Combine(Path.GetTempPath(), "lakehold-storage-scope", suffix);
        Directory.CreateDirectory(root);

        var configuredOptions = new LakehouseOptions
        {
            BackupRoot = "s3://lakehold-backups",
            EjectRoot = "s3://lakehold-ejects",
        };
        configuredOptions.StorageProfiles["remote"] = new ParquetStorageProfileOptions
        {
            Kind = ParquetStorageKind.S3,
            KeyId = "test-key",
            Secret = "test-secret",
        };
        var options = Options.Create(configuredOptions);
        var configurator = new DucklingSessionConfigurator(new ConfigurationBuilder().Build(), options);
        await using var pool = new DucklingPool(options, NullLoggerFactory.Instance, [configurator]);
        var descriptor = new CatalogDescriptor(
            "analytics",
            CatalogMetadataKind.LocalFile,
            Path.Combine(root, "metadata.ducklake"),
            "s3://lakehold-data/acme/analytics",
            SecretName: "lh_store_scope",
            TenantKey: "acme",
            CatalogId: 1,
            StorageKind: ParquetStorageKind.S3,
            StorageProfile: "remote");

        try
        {
            var session = await pool.GetOrStartAsync(descriptor, configure: null, CancellationToken.None);
            var secrets = await session.ExecuteQueryAsync(
                "SELECT name, unnest(scope) AS scope FROM duckdb_secrets() "
                + "WHERE name LIKE 'lh_store_scope%' ORDER BY name",
                CancellationToken.None);

            Assert.Equal(
                [
                    ("lh_store_scope", "s3://lakehold-data/acme/analytics/"),
                    ("lh_store_scope_backup", "s3://lakehold-backups/acme/analytics/"),
                    ("lh_store_scope_eject", "s3://lakehold-ejects/acme/analytics/"),
                ],
                secrets.Rows.Select(row => (
                    Convert.ToString(row[0], System.Globalization.CultureInfo.InvariantCulture)!,
                    Convert.ToString(row[1], System.Globalization.CultureInfo.InvariantCulture)!)));
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // Temp cleanup does not affect the scope assertion.
            }
        }
    }

    [SkippableFact]
    public async Task A_fresh_Postgres_schema_migrates_idempotently_and_generates_ids()
    {
        var configured = Environment.GetEnvironmentVariable("LAKEHOLD_TEST_POSTGRES");
        Skip.If(
            string.IsNullOrWhiteSpace(configured),
            "Set LAKEHOLD_TEST_POSTGRES to run PostgreSQL control-plane migration tests.");

        var schema = "lh_cp_" + Guid.NewGuid().ToString("N");
        var connectionString = new NpgsqlConnectionStringBuilder(configured!)
        {
            SearchPath = schema,
        }.ConnectionString;

        await using var administrative = new NpgsqlConnection(configured);
        await administrative.OpenAsync();
        await using (var create = administrative.CreateCommand())
        {
            create.CommandText = $"CREATE SCHEMA {schema}";
            await create.ExecuteNonQueryAsync();
        }

        try
        {
            var services = new ServiceCollection()
                .AddDbContext<ControlPlaneContext>(options => options.UseNpgsql(connectionString))
                .BuildServiceProvider();
            await using var provider = services;
            await ControlPlaneDatabase.MigrateAsync(provider, NullLogger.Instance);
            await ControlPlaneDatabase.MigrateAsync(provider, NullLogger.Instance);
            await using var scope = provider.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<ControlPlaneContext>();

            context.Tenants.Add(new Tenant
            {
                Slug = "migration-test",
                DisplayName = "Migration test",
                CreatedUtc = DateTimeOffset.UtcNow,
            });
            await context.SaveChangesAsync();

            Assert.True(await context.Tenants.Select(tenant => tenant.Id).SingleAsync() > 0);
            Assert.Equal(
                [
                    "20260728195348_InitialPostgresControlPlane",
                    "20260728232510_AddCatalogScopedSavedQueries",
                    "20260729173815_AddSystemSettings",
                    "20260729194500_AddBrowserAuthentication",
                    "20260730163059_AddDurableCdcDeliveries",
                    "20260730164352_AddCdcConsumerWatermarks",
                    "20260802111047_AddQueryLanguages",
                ],
                await context.Database.GetAppliedMigrationsAsync());
            Assert.Equal(0, await context.DataProtectionKeys.CountAsync());
        }
        finally
        {
            await using var drop = administrative.CreateCommand();
            drop.CommandText = $"DROP SCHEMA {schema} CASCADE";
            await drop.ExecuteNonQueryAsync();
        }
    }

    [SkippableFact]
    public async Task Product_session_configuration_attaches_Postgres_metadata_and_local_Parquet()
    {
        var configured = Environment.GetEnvironmentVariable("LAKEHOLD_TEST_POSTGRES");
        Skip.If(
            string.IsNullOrWhiteSpace(configured),
            "Set LAKEHOLD_TEST_POSTGRES to run PostgreSQL DuckLake session tests.");

        var suffix = Guid.NewGuid().ToString("N")[..12];
        var schema = "lh_session_" + suffix;
        var root = Path.Combine(Path.GetTempPath(), "lakehold-pg-session", suffix);
        Directory.CreateDirectory(root);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DuckLakeMetadata"] = configured,
            })
            .Build();
        var lakehouseOptions = Options.Create(new LakehouseOptions
        {
            BackupRoot = Path.Combine(root, "backups"),
        });
        var configurator = new DucklingSessionConfigurator(configuration, lakehouseOptions);
        await using var pool = new DucklingPool(
            lakehouseOptions,
            NullLoggerFactory.Instance,
            [configurator]);
        var descriptor = new CatalogDescriptor(
            "analytics",
            CatalogMetadataKind.Postgres,
            "lh_dl_" + suffix,
            root,
            MetadataSchema: schema,
            MetadataSecretName: "lh_pg_" + suffix,
            TenantKey: "integration",
            CatalogId: 1);

        try
        {
            var session = await pool.GetOrStartAsync(descriptor, configure: null, CancellationToken.None);
            await session.ExecuteQueryAsync("CREATE TABLE proof (id BIGINT)", CancellationToken.None);
            await session.ExecuteQueryAsync("INSERT INTO proof VALUES (1), (2)", CancellationToken.None);
            var result = await session.ExecuteQueryAsync("SELECT count(*) FROM proof", CancellationToken.None);

            Assert.Equal(2L, Convert.ToInt64(result.Rows[0][0], System.Globalization.CultureInfo.InvariantCulture));

            var visibleBeforeBackup = await session.ExecuteQueryAsync(
                $"SELECT count(*) FROM duckdb_secrets() WHERE name IN "
                + $"('lh_pg_{suffix}', 'lh_dl_{suffix}')",
                CancellationToken.None);
            Assert.Equal(
                0L,
                Convert.ToInt64(
                    visibleBeforeBackup.Rows[0][0],
                    System.Globalization.CultureInfo.InvariantCulture));

            Assert.True(await MaintenanceLease.TryAcquireAsync(
                session,
                "backup",
                "integration-node",
                TimeSpan.FromMinutes(1),
                CancellationToken.None));
            await MaintenanceLease.ReleaseAsync(
                session,
                "backup",
                "integration-node",
                CancellationToken.None);

            var backup = await LakehouseMaintenance.BackupCatalogAsync(
                session,
                lakehouseOptions.Value,
                CancellationToken.None);
            Assert.Contains("exported", backup.Detail, StringComparison.Ordinal);

            var visibleAfterBackup = await session.ExecuteQueryAsync(
                $"SELECT count(*) FROM duckdb_secrets() WHERE name IN "
                + $"('lh_pg_{suffix}', 'lh_dl_{suffix}')",
                CancellationToken.None);
            Assert.Equal(
                0L,
                Convert.ToInt64(
                    visibleAfterBackup.Rows[0][0],
                    System.Globalization.CultureInfo.InvariantCulture));
        }
        finally
        {
            await pool.DisposeAsync();
            await using var administrative = new NpgsqlConnection(configured);
            await administrative.OpenAsync();
            await using var drop = administrative.CreateCommand();
            drop.CommandText = $"DROP SCHEMA IF EXISTS {schema} CASCADE";
            await drop.ExecuteNonQueryAsync();
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // Temp cleanup does not affect the integration assertion.
            }
        }
    }
}

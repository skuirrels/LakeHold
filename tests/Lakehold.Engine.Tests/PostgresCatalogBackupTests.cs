using DuckDB.EFCoreProvider.Infrastructure;
using DuckDB.NET.Data;
using Lakehold.Engine.Catalog;
using Lakehold.Engine.Configuration;
using Lakehold.Engine.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Globalization;
using Xunit;

namespace Lakehold.Engine.Tests;

/// <summary>
///     Cover for backing up a catalog whose metadata lives in PostgreSQL.
/// </summary>
/// <remarks>
///     <para>
///         PostgreSQL needs opposite handling to a local file: DuckLake attaches nothing queryable
///         behind the catalog, so the metadata has to be attached separately. That difference is
///         invisible to the type system and only shows up against a real server, so these tests run
///         against one.
///     </para>
///     <para>
///         Gated on <c>LAKEHOLD_TEST_POSTGRES</c> — a libpq connection string — and skipped when it
///         is unset, so the default <c>dotnet test</c> run needs no external services.
///     </para>
/// </remarks>
public sealed class PostgresCatalogBackupTests : IAsyncLifetime
{
    private const string ConnectionVariable = "LAKEHOLD_TEST_POSTGRES";
    private const string CredentialSecret = "lakehold_test_pgcreds";
    private const string ProfileSecret = "lakehold_test_pgprofile";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "lakehold-pg-tests", Guid.NewGuid().ToString("N"));
    private string? _connection;
    private LakehouseOptions _options = null!;
    private CatalogDescriptor _catalog = null!;
    private DucklingPool? _pool;

    public async Task InitializeAsync()
    {
        _connection = Environment.GetEnvironmentVariable(ConnectionVariable);
        if (string.IsNullOrWhiteSpace(_connection))
        {
            return;
        }

        Directory.CreateDirectory(_root);

        // Every test gets a fresh temp data path but they all share one PostgreSQL database, and
        // DuckLake refuses to attach a catalog whose recorded data path does not match. Resetting
        // the metadata is what keeps the tests independent of each other's leftovers.
        await ResetMetadataAsync();

        _options = new LakehouseOptions
        {
            DataRoot = Path.Combine(_root, "data"),
            BackupRoot = Path.Combine(_root, "backups"),
            BackupRetainCount = 3,
        };

        _options.Extensions.Add("postgres");

        _catalog = new CatalogDescriptor(
            "pglake",
            CatalogMetadataKind.Postgres,
            ProfileSecret,
            Path.Combine(_root, "data"),
            MetadataSecretName: CredentialSecret);

        Directory.CreateDirectory(_catalog.DataPath);
        _pool = new DucklingPool(Options.Create(_options), NullLoggerFactory.Instance);

        var duckling = await _pool.GetOrStartAsync(_catalog, CreateSecrets, CancellationToken.None);
        await Run(duckling, "CREATE TABLE people (id BIGINT, name VARCHAR)");
        await Run(duckling, "INSERT INTO people SELECT i, 'name' || i FROM range(3000) t(i)");
        await Run(duckling, "DELETE FROM people WHERE id < 7");
        await Run(duckling, "CALL ducklake_flush_inlined_data('pglake')");
    }

    public async Task DisposeAsync()
    {
        if (_pool is not null)
        {
            await _pool.DisposeAsync();
        }

        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Temp cleanup failing must not fail the test run.
        }
    }

    [SkippableFact]
    public async Task Postgres_metadata_backs_up_and_restores_into_a_local_catalog()
    {
        Skip.If(
            string.IsNullOrWhiteSpace(_connection),
            $"Set {ConnectionVariable} to a libpq connection string to run PostgreSQL tests.");

        var duckling = await _pool!.GetOrStartAsync(_catalog, CreateSecrets, CancellationToken.None);
        var liveRows = await ScalarAsync(duckling, "SELECT count(*) FROM people");
        var snapshots = await ScalarAsync(duckling, "SELECT count(*) FROM ducklake_snapshots('pglake')");

        var backup = await duckling.InvokeAsync(
            ct => CatalogBackup.WriteAsync(duckling, _options, TimeProvider.System, ct),
            CancellationToken.None);

        Assert.True(backup.TableCount > 0, "a PostgreSQL catalog must export its metadata tables");

        var generations = await CatalogRestore.ListGenerationsAsync(
            _options, _catalog.CatalogName, configure: null, CancellationToken.None);
        var generation = Assert.Single(generations);

        // Provenance is recorded so an operator restoring months later can see the backup came from
        // PostgreSQL even though it restores into a plain DuckDB file.
        Assert.Equal(CatalogMetadataKind.Postgres, generation.Manifest!.MetadataKind);
        Assert.Equal("public", generation.Manifest.MetadataSchema);

        // The point of the exercise: the restored catalog is a local file, so this is an exit path
        // from PostgreSQL and not merely a copy of it.
        var target = Path.Combine(_root, "restored-from-pg.ducklake");
        var restore = await CatalogRestore.RestoreAsync(
            _options, _catalog.CatalogName, generation: null, target, _catalog.DataPath, CancellationToken.None);

        Assert.Equal(generation.Manifest.Tables.Count, restore.TablesRestored);

        var restored = new CatalogDescriptor(
            "restoredpg", CatalogMetadataKind.LocalFile, target, _catalog.DataPath);

        await using var pool = new DucklingPool(Options.Create(_options), NullLoggerFactory.Instance);
        var session = await pool.GetOrStartAsync(restored, configure: null, CancellationToken.None);

        Assert.Equal(liveRows, await ScalarAsync(session, "SELECT count(*) FROM people"));
        Assert.Equal("0", await ScalarAsync(session, "SELECT count(*) FROM people WHERE id < 7"));
        Assert.Equal(
            "0",
            await ScalarAsync(session, "SELECT count(*) FROM (SELECT id FROM people GROUP BY id HAVING count(*) > 1)"));
        Assert.Equal(snapshots, await ScalarAsync(session, "SELECT count(*) FROM ducklake_snapshots('restoredpg')"));
    }

    [SkippableFact]
    public async Task Only_one_node_can_hold_a_maintenance_lease()
    {
        Skip.If(
            string.IsNullOrWhiteSpace(_connection),
            $"Set {ConnectionVariable} to a libpq connection string to run PostgreSQL tests.");

        var duckling = await _pool!.GetOrStartAsync(_catalog, CreateSecrets, CancellationToken.None);
        var lease = TimeSpan.FromMinutes(5);

        var first = await MaintenanceLease.TryAcquireAsync(
            duckling, "backup", "node-a", lease, CancellationToken.None);
        var second = await MaintenanceLease.TryAcquireAsync(
            duckling, "backup", "node-b", lease, CancellationToken.None);

        // The whole point: two nodes firing the same cron must not both run the sweep. For backup
        // that would race duplicate generations through retention.
        Assert.True(first, "the first node must win the lease");
        Assert.False(second, "a second node must not take a lease that is still held");

        // A different job is independent, so one slow compaction cannot block every backup.
        Assert.True(await MaintenanceLease.TryAcquireAsync(
            duckling, "flush", "node-b", lease, CancellationToken.None));

        // Releasing hands over immediately rather than making the next node wait out the expiry.
        await MaintenanceLease.ReleaseAsync(duckling, "backup", "node-a", CancellationToken.None);
        Assert.True(await MaintenanceLease.TryAcquireAsync(
            duckling, "backup", "node-b", lease, CancellationToken.None));
    }

    [SkippableFact]
    public async Task The_lease_does_not_appear_in_a_backup()
    {
        Skip.If(
            string.IsNullOrWhiteSpace(_connection),
            $"Set {ConnectionVariable} to a libpq connection string to run PostgreSQL tests.");

        var duckling = await _pool!.GetOrStartAsync(_catalog, CreateSecrets, CancellationToken.None);
        await MaintenanceLease.TryAcquireAsync(
            duckling, "backup", "node-a", TimeSpan.FromMinutes(5), CancellationToken.None);

        var backup = await duckling.InvokeAsync(
            ct => CatalogBackup.WriteAsync(duckling, _options, TimeProvider.System, ct),
            CancellationToken.None);

        var generations = await CatalogRestore.ListGenerationsAsync(
            _options, _catalog.CatalogName, configure: null, CancellationToken.None);

        // The lease lives in its own schema so it cannot collide with a DuckLake migration and does
        // not ride along into a backup, where restoring it would reinstate a stale claim.
        Assert.DoesNotContain(
            generations[0].Manifest!.Tables,
            t => t.Name.Contains("lease", StringComparison.OrdinalIgnoreCase));
        Assert.True(backup.TableCount > 0);
    }

    /// <summary>
    ///     Installs the two secrets a PostgreSQL-backed catalog needs, in connection configuration.
    /// </summary>
    /// <remarks>
    ///     This is the shape the real deployment uses, and it is the reason the credential never
    ///     appears on the descriptor: the catalog record names secrets, and the secrets are created
    ///     here on the session's own connection.
    /// </remarks>
    private void CreateSecrets(DuckDBDbContextOptionsBuilder duckDb)
    {
        var parts = ParseConnection(_connection!);
        var dataPath = _catalog.DataPath.Replace("'", "''", StringComparison.Ordinal);

        duckDb.ConfigureConnection(connection =>
        {
            using var command = connection.CreateCommand();
            // Doubled braces are the interpolation markers here, so DuckDB's MAP{...} literal can
            // stay written the way DuckDB spells it.
            command.CommandText = $$"""
                CREATE OR REPLACE SECRET {{CredentialSecret}} (
                    TYPE postgres,
                    HOST '{{parts["host"]}}',
                    PORT {{parts["port"]}},
                    DATABASE '{{parts["dbname"]}}',
                    USER '{{parts["user"]}}',
                    PASSWORD '{{parts["password"]}}');
                CREATE OR REPLACE SECRET {{ProfileSecret}} (
                    TYPE ducklake,
                    METADATA_PATH '',
                    DATA_PATH '{{dataPath}}/',
                    METADATA_PARAMETERS MAP{'TYPE': 'postgres', 'SECRET': '{{CredentialSecret}}'});
                """;
            command.ExecuteNonQuery();
        });
    }

    /// <summary>Drops everything this suite creates in the shared PostgreSQL database.</summary>
    private async Task ResetMetadataAsync()
    {
        var parts = ParseConnection(_connection!);

        await using var connection = new DuckDBConnection("Data Source=:memory:");
        await connection.OpenAsync();

        await Execute(connection, "INSTALL postgres; LOAD postgres;");
        await Execute(
            connection,
            $"""
            CREATE OR REPLACE SECRET reset_creds (
                TYPE postgres,
                HOST '{parts["host"]}',
                PORT {parts["port"]},
                DATABASE '{parts["dbname"]}',
                USER '{parts["user"]}',
                PASSWORD '{parts["password"]}');
            """);
        await Execute(connection, "ATTACH '' AS reset_pg (TYPE postgres, SECRET reset_creds);");

        foreach (var statement in new[]
        {
            "DROP SCHEMA IF EXISTS public CASCADE",
            "CREATE SCHEMA public",
            $"DROP SCHEMA IF EXISTS {MaintenanceLease.SchemaName} CASCADE",
        })
        {
            await Execute(connection, $"CALL postgres_execute('reset_pg', '{statement}')");
        }
    }

    private static async Task Execute(DuckDBConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static Dictionary<string, string> ParseConnection(string connection)
    {
        var parsed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in connection.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var split = pair.Split('=', 2);
            if (split.Length == 2)
            {
                parsed[split[0]] = split[1];
            }
        }

        return parsed;
    }

    private static async Task Run(Duckling duckling, string sql)
        => await duckling.ExecuteQueryAsync(sql, CancellationToken.None);

    private static async Task<string> ScalarAsync(Duckling duckling, string sql)
    {
        var result = await duckling.ExecuteQueryAsync(sql, CancellationToken.None);
        return Convert.ToString(result.Rows[0][0], CultureInfo.InvariantCulture) ?? string.Empty;
    }
}

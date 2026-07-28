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
///     Cover for the storage view against the two deployments the local-file fixture cannot imitate:
///     data in an object store, and metadata in PostgreSQL.
/// </summary>
/// <remarks>
///     <para>
///         Both differences are invisible to the type checker, and both land squarely on this surface.
///         The rollup joins DuckLake's metadata tables through an alias that
///         <see cref="MetadataExporter.ResolveMetadataAliasAsync"/> discovers at run time, and
///         PostgreSQL is the configuration where DuckLake attaches nothing queryable behind the
///         catalog — so an alias that resolves for a local file proves nothing about one that does
///         not. The file list, meanwhile, reports <em>paths</em>, and an object store returns URIs
///         where a local catalog returns filesystem paths.
///     </para>
///     <para>
///         Gated the same way the backup suites are, and skipped when their variables are unset, so
///         the default <c>dotnet test</c> run needs no external services.
///     </para>
/// </remarks>
public sealed class ObjectStoreStorageTests : IAsyncLifetime
{
    private const string EndpointVariable = "LAKEHOLD_TEST_S3_ENDPOINT";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "lakehold-s3-storage", Guid.NewGuid().ToString("N"));
    private readonly string _prefix = Guid.NewGuid().ToString("N");
    private string? _endpoint;
    private string _key = string.Empty;
    private string _secret = string.Empty;
    private string _bucket = string.Empty;
    private CatalogDescriptor _catalog = null!;
    private DucklingPool? _pool;

    public async Task InitializeAsync()
    {
        _endpoint = Environment.GetEnvironmentVariable(EndpointVariable);
        if (string.IsNullOrWhiteSpace(_endpoint))
        {
            return;
        }

        _key = Environment.GetEnvironmentVariable("LAKEHOLD_TEST_S3_KEY") ?? string.Empty;
        _secret = Environment.GetEnvironmentVariable("LAKEHOLD_TEST_S3_SECRET") ?? string.Empty;
        _bucket = Environment.GetEnvironmentVariable("LAKEHOLD_TEST_S3_BUCKET") ?? string.Empty;

        Directory.CreateDirectory(_root);

        // The inverse of ObjectStoreBackupTests: there the data was local and the backup went to the
        // bucket, because the backup's path handling was under test. Here the *data* is in the bucket,
        // because that is what the file list reports.
        var options = new LakehouseOptions { DataRoot = $"s3://{_bucket}/{_prefix}" };
        _catalog = new CatalogDescriptor(
            "s3storage",
            CatalogMetadataKind.LocalFile,
            Path.Combine(_root, "s3storage.ducklake"),
            $"s3://{_bucket}/{_prefix}/data");

        _pool = new DucklingPool(Options.Create(options), NullLoggerFactory.Instance);

        var duckling = await _pool.GetOrStartAsync(_catalog, CreateSecret, CancellationToken.None);
        await Run(duckling, "CREATE TABLE readings AS SELECT i AS id FROM range(20000) t(i)");
        await Run(duckling, "DELETE FROM readings WHERE id < 500");
        await Run(duckling, "CREATE TABLE pending (id BIGINT)");
        await Run(duckling, "INSERT INTO pending VALUES (1), (2)");
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
            // Temp cleanup failing must not fail the run. The bucket prefix is left behind; the
            // test bucket is disposable and DuckDB cannot delete from an object store anyway.
        }
    }

    [SkippableFact]
    public async Task The_rollup_is_correct_when_the_data_lives_in_a_bucket()
    {
        Skip.If(
            string.IsNullOrWhiteSpace(_endpoint),
            $"Set {EndpointVariable}, _KEY, _SECRET and _BUCKET to run object-store tests.");

        var duckling = await _pool!.GetOrStartAsync(_catalog, CreateSecret, CancellationToken.None);
        var storage = await StorageBrowser.ReadAsync(duckling, CancellationToken.None);

        // Row counts come from count(*), which reads Parquet footers — over the network here, which
        // is the half of that design decision a local fixture cannot exercise.
        var readings = Assert.Single(storage.Tables, t => t.TableName == "readings");
        Assert.Equal(await CountAsync(duckling, "readings"), readings.RowCount);
        Assert.True(readings.FileCount > 0);
        Assert.True(readings.FileSizeBytes > 0);

        // Inlined rows are in the metadata catalog, which is local here, so a bucket-backed table can
        // still report zero files with data in it.
        var pending = Assert.Single(storage.Tables, t => t.TableName == "pending");
        Assert.Equal(0, pending.FileCount);
        Assert.Equal(2, pending.RowCount);
        Assert.Equal(2, pending.InlinedRows);
    }

    [SkippableFact]
    public async Task The_file_list_reports_object_store_uris_rather_than_filesystem_paths()
    {
        Skip.If(
            string.IsNullOrWhiteSpace(_endpoint),
            $"Set {EndpointVariable}, _KEY, _SECRET and _BUCKET to run object-store tests.");

        var duckling = await _pool!.GetOrStartAsync(_catalog, CreateSecret, CancellationToken.None);
        var files = await StorageBrowser.ListFilesAsync(
            duckling, "main", "readings", snapshotId: null, maxRows: 100, CancellationToken.None);

        Assert.NotEmpty(files.Files);

        // The reason this suite exists. A bucket has no directories and its keys are not Path-shaped,
        // so anything downstream that treats a data file as a local path — the panel splitting a
        // directory from a file name, an operator pasting one into a reader — has to be looking at a
        // URI here, not at something a local run would have produced.
        Assert.All(files.Files, f => Assert.StartsWith("s3://", f.DataFile, StringComparison.Ordinal));
        Assert.All(files.Files, f => Assert.DoesNotContain('\\', f.DataFile));
        Assert.All(files.Files, f => Assert.True(f.DataFileSizeBytes > 0));

        // The delete file is in the same bucket, and its pairing has to survive the round trip too.
        var deleted = Assert.Single(files.Files, f => f.DeleteFile is not null);
        Assert.StartsWith("s3://", deleted.DeleteFile!, StringComparison.Ordinal);
        Assert.True(deleted.DeleteFileSizeBytes > 0);
    }

    [SkippableFact]
    public async Task Table_detail_and_profiles_read_logical_rows_from_object_storage()
    {
        Skip.If(
            string.IsNullOrWhiteSpace(_endpoint),
            $"Set {EndpointVariable}, _KEY, _SECRET and _BUCKET to run object-store tests.");

        var duckling = await _pool!.GetOrStartAsync(_catalog, CreateSecret, CancellationToken.None);
        var detail = await TableInspector.ReadAsync(
            duckling, "main", "readings", CancellationToken.None);
        var profile = await ColumnProfiler.ReadAsync(
            duckling, "main", "readings", null, CancellationToken.None);

        Assert.Equal(19_500, detail.Storage?.RowCount);
        Assert.Equal(19_500, profile.RowCount);
        Assert.Equal("500", Assert.Single(profile.Columns).Minimum);
    }

    /// <summary>Installs the bucket credential on the session's own connection.</summary>
    private void CreateSecret(DuckDBDbContextOptionsBuilder duckDb)
    {
        var host = _endpoint!.Replace("http://", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("https://", string.Empty, StringComparison.OrdinalIgnoreCase);
        var useSsl = _endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? "true" : "false";

        duckDb.ConfigureConnection(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"""
                CREATE OR REPLACE SECRET lakehold_test_s3 (
                    TYPE S3,
                    KEY_ID '{_key}',
                    SECRET '{_secret}',
                    ENDPOINT '{host}',
                    USE_SSL {useSsl},
                    URL_STYLE 'path');
                """;
            command.ExecuteNonQuery();
        });
    }

    private static Task<QueryResult> Run(Duckling duckling, string sql) => duckling.ExecuteQueryAsync(sql, CancellationToken.None);

    private static async Task<long> CountAsync(Duckling duckling, string table)
    {
        var result = await duckling.ExecuteQueryAsync($"SELECT count(*) FROM {table}", CancellationToken.None);
        return Convert.ToInt64(result.Rows[0][0], CultureInfo.InvariantCulture);
    }
}

/// <summary>
///     Cover for the storage view against a catalog whose metadata lives in PostgreSQL.
/// </summary>
/// <remarks>
///     Shares a collection with <see cref="PostgresCatalogBackupTests"/> because both reset the same
///     database's <c>public</c> schema on the way in, and xUnit runs test classes in parallel by
///     default. Without the collection the two would race and fail intermittently in a way that looks
///     like a product bug.
/// </remarks>
[Collection(PostgresMetadata.CollectionName)]
public sealed class PostgresStorageTests : IAsyncLifetime
{
    private const string ConnectionVariable = "LAKEHOLD_TEST_POSTGRES";
    private const string CredentialSecret = "lakehold_storage_pgcreds";
    private const string ProfileSecret = "lakehold_storage_pgprofile";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "lakehold-pg-storage", Guid.NewGuid().ToString("N"));
    private string? _connection;
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
        await ResetMetadataAsync();

        var options = new LakehouseOptions { DataRoot = Path.Combine(_root, "data") };
        options.Extensions.Add("postgres");

        _catalog = new CatalogDescriptor(
            "pgstorage",
            CatalogMetadataKind.Postgres,
            ProfileSecret,
            Path.Combine(_root, "data"),
            MetadataSecretName: CredentialSecret);

        Directory.CreateDirectory(_catalog.DataPath);
        _pool = new DucklingPool(Options.Create(options), NullLoggerFactory.Instance);

        var duckling = await _pool.GetOrStartAsync(_catalog, CreateSecrets, CancellationToken.None);
        await Run(duckling, "CREATE TABLE readings AS SELECT i AS id FROM range(20000) t(i)");
        await Run(duckling, "DELETE FROM readings WHERE id < 500");
        await Run(duckling, "CREATE SCHEMA warm");
        await Run(duckling, "CREATE TABLE warm.pending (id BIGINT)");
        await Run(duckling, "INSERT INTO warm.pending VALUES (1), (2)");
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
            // Temp cleanup failing must not fail the run.
        }
    }

    [SkippableFact]
    public async Task The_metadata_alias_resolves_and_the_rollup_is_correct()
    {
        Skip.If(
            string.IsNullOrWhiteSpace(_connection),
            $"Set {ConnectionVariable} to a libpq connection string to run PostgreSQL tests.");

        var duckling = await _pool!.GetOrStartAsync(_catalog, CreateSecrets, CancellationToken.None);
        var storage = await StorageBrowser.ReadAsync(duckling, CancellationToken.None);

        // This is the assertion the whole class is for. The rollup joins four ducklake_* metadata
        // tables through an alias discovered at run time, and PostgreSQL is the configuration where
        // DuckLake attaches nothing queryable behind the catalog. If the alias resolved to something
        // that is not there, this read raises rather than returning a wrong number — but only against
        // a real server.
        var readings = Assert.Single(storage.Tables, t => t.TableName == "readings");
        Assert.Equal("main", readings.SchemaName);
        Assert.Equal(await CountAsync(duckling, "readings"), readings.RowCount);
        Assert.True(readings.FileCount > 0);

        // Schema names come from ducklake_schema in the PostgreSQL catalog, so a non-main schema is
        // what proves the join found real rows rather than defaulting.
        var pending = Assert.Single(storage.Tables, t => t.TableName == "pending");
        Assert.Equal("warm", pending.SchemaName);
        Assert.Equal(2, pending.RowCount);
        Assert.Equal(2, pending.InlinedRows);
        Assert.Equal(0, pending.FileCount);

        // The metadata catalog's own tables are not tenant data and must never appear as such.
        Assert.DoesNotContain(storage.Tables, t => t.TableName.StartsWith("ducklake_", StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task The_file_list_reaches_a_table_whose_metadata_is_in_postgres()
    {
        Skip.If(
            string.IsNullOrWhiteSpace(_connection),
            $"Set {ConnectionVariable} to a libpq connection string to run PostgreSQL tests.");

        var duckling = await _pool!.GetOrStartAsync(_catalog, CreateSecrets, CancellationToken.None);
        var files = await StorageBrowser.ListFilesAsync(
            duckling, "main", "readings", snapshotId: null, maxRows: 100, CancellationToken.None);

        Assert.NotEmpty(files.Files);
        Assert.All(files.Files, f => Assert.True(f.DataFileSizeBytes > 0));

        // Data is local even though the metadata is not, so the paths are filesystem paths under the
        // catalog's own data root — the pairing being what travels through PostgreSQL.
        Assert.All(files.Files, f => Assert.Contains(".parquet", f.DataFile, StringComparison.Ordinal));
        var deleted = Assert.Single(files.Files, f => f.DeleteFile is not null);
        Assert.True(deleted.DeleteFileSizeBytes > 0);
    }

    [SkippableFact]
    public async Task Table_detail_and_profiles_resolve_postgres_metadata()
    {
        Skip.If(
            string.IsNullOrWhiteSpace(_connection),
            $"Set {ConnectionVariable} to a libpq connection string to run PostgreSQL tests.");

        var duckling = await _pool!.GetOrStartAsync(_catalog, CreateSecrets, CancellationToken.None);
        var detail = await TableInspector.ReadAsync(
            duckling, "warm", "pending", CancellationToken.None);
        var profile = await ColumnProfiler.ReadAsync(
            duckling, "warm", "pending", null, CancellationToken.None);

        Assert.Equal(2, detail.Storage?.RowCount);
        Assert.Equal(2, profile.RowCount);
        Assert.Equal("1", Assert.Single(profile.Columns).Minimum);
    }

    /// <summary>Installs the two secrets a PostgreSQL-backed catalog needs, in connection configuration.</summary>
    private void CreateSecrets(DuckDBDbContextOptionsBuilder duckDb)
    {
        var parts = PostgresMetadata.ParseConnection(_connection!);
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

    private Task ResetMetadataAsync() => PostgresMetadata.ResetAsync(_connection!);

    private static Task<QueryResult> Run(Duckling duckling, string sql) => duckling.ExecuteQueryAsync(sql, CancellationToken.None);

    private static async Task<long> CountAsync(Duckling duckling, string table)
    {
        var result = await duckling.ExecuteQueryAsync($"SELECT count(*) FROM {table}", CancellationToken.None);
        return Convert.ToInt64(result.Rows[0][0], CultureInfo.InvariantCulture);
    }
}

/// <summary>
///     Shared machinery for the suites backed by the one PostgreSQL database compose brings up.
/// </summary>
/// <remarks>
///     They all reset the same <c>public</c> schema on the way in, so they must not run at the same
///     time. <see cref="CollectionName"/> is what serialises them.
/// </remarks>
[CollectionDefinition(CollectionName)]
public sealed class PostgresMetadata
{
    /// <summary>Names the xUnit collection every PostgreSQL-backed suite joins.</summary>
    public const string CollectionName = "postgres-metadata";

    /// <summary>Drops everything these suites create in the shared database.</summary>
    internal static async Task ResetAsync(string connection)
    {
        var parts = ParseConnection(connection);

        await using var duck = new DuckDBConnection("Data Source=:memory:");
        await duck.OpenAsync();

        await Execute(duck, "INSTALL postgres; LOAD postgres;");
        await Execute(
            duck,
            $"""
            CREATE OR REPLACE SECRET reset_creds (
                TYPE postgres,
                HOST '{parts["host"]}',
                PORT {parts["port"]},
                DATABASE '{parts["dbname"]}',
                USER '{parts["user"]}',
                PASSWORD '{parts["password"]}');
            """);
        await Execute(duck, "ATTACH '' AS reset_pg (TYPE postgres, SECRET reset_creds);");

        foreach (var statement in new[]
        {
            "DROP SCHEMA IF EXISTS public CASCADE",
            "CREATE SCHEMA public",
            $"DROP SCHEMA IF EXISTS {MaintenanceLease.SchemaName} CASCADE",
        })
        {
            await Execute(duck, $"CALL postgres_execute('reset_pg', '{statement}')");
        }
    }

    /// <summary>Splits a libpq connection string into its keywords.</summary>
    internal static Dictionary<string, string> ParseConnection(string connection)
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

    private static async Task Execute(DuckDBConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}

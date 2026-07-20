using DuckDB.EFCoreProvider.Infrastructure;
using Lakehold.Engine.Catalog;
using Lakehold.Engine.Configuration;
using Lakehold.Engine.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Globalization;
using Xunit;

namespace Lakehold.Engine.Tests;

/// <summary>
///     Cover for backups that live in an object store rather than on local disk.
/// </summary>
/// <remarks>
///     <para>
///         Bring-your-own-bucket is the product's headline claim, so the backup path has to work
///         against a real S3 API and not just a local directory. Object stores differ in ways the
///         type system hides: there are no directories to enumerate, paths are not
///         <see cref="Path"/>-shaped, and there is no delete.
///     </para>
///     <para>
///         Gated on <c>LAKEHOLD_TEST_S3_ENDPOINT</c> (plus key, secret, and bucket) and skipped when
///         unset, so the default <c>dotnet test</c> run needs no external services.
///     </para>
/// </remarks>
public sealed class ObjectStoreBackupTests : IAsyncLifetime
{
    private const string EndpointVariable = "LAKEHOLD_TEST_S3_ENDPOINT";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "lakehold-s3-tests", Guid.NewGuid().ToString("N"));
    private readonly string _prefix = Guid.NewGuid().ToString("N");
    private string? _endpoint;
    private string _key = string.Empty;
    private string _secret = string.Empty;
    private string _bucket = string.Empty;
    private LakehouseOptions _options = null!;
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

        // Data stays local while backups go to the bucket, which isolates what is under test: the
        // backup's own path handling, not DuckLake's.
        _options = new LakehouseOptions
        {
            DataRoot = Path.Combine(_root, "data"),
            BackupRoot = $"s3://{_bucket}/{_prefix}/backups",
            BackupRetainCount = 2,
        };

        _catalog = new CatalogDescriptor(
            "s3lake",
            CatalogMetadataKind.LocalFile,
            Path.Combine(_root, "s3test.ducklake"),
            Path.Combine(_root, "data"));

        Directory.CreateDirectory(_catalog.DataPath);
        _pool = new DucklingPool(Options.Create(_options), NullLoggerFactory.Instance);

        var duckling = await _pool.GetOrStartAsync(_catalog, CreateSecret, CancellationToken.None);
        await duckling.ExecuteQueryAsync("CREATE TABLE people (id BIGINT, name VARCHAR)", CancellationToken.None);
        await duckling.ExecuteQueryAsync(
            "INSERT INTO people SELECT i, 'name' || i FROM range(4000) t(i)", CancellationToken.None);
        await duckling.ExecuteQueryAsync("DELETE FROM people WHERE id < 11", CancellationToken.None);
        await duckling.ExecuteQueryAsync("CALL ducklake_flush_inlined_data('s3lake')", CancellationToken.None);
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
    public async Task Backup_to_a_bucket_can_be_listed_and_restored()
    {
        Skip.If(
            string.IsNullOrWhiteSpace(_endpoint),
            $"Set {EndpointVariable}, _KEY, _SECRET and _BUCKET to run object-store tests.");

        var duckling = await _pool!.GetOrStartAsync(_catalog, CreateSecret, CancellationToken.None);
        var liveRows = await ScalarAsync(duckling, "SELECT count(*) FROM people");

        var backup = await duckling.InvokeAsync(
            ct => CatalogBackup.WriteAsync(duckling, _options, TimeProvider.System, ct),
            CancellationToken.None);

        Assert.StartsWith("s3://", backup.Location, StringComparison.Ordinal);
        Assert.True(backup.TableCount > 0);

        // Size needs a listing request on an object store, so it is reported as unknown rather than
        // guessed at.
        Assert.Null(backup.Bytes);

        // Listing a bucket means globbing keys, not enumerating directories.
        var generations = await CatalogRestore.ListGenerationsAsync(
            _options, _catalog.CatalogName, CreateSecret, CancellationToken.None);

        var generation = Assert.Single(generations);
        Assert.True(generation.IsComplete, "the manifest must be readable back out of the bucket");
        Assert.Equal(backup.TableCount, generation.Manifest!.Tables.Count);

        // The manifest crosses to the bucket as a single unquoted CSV value, so this is where a
        // multi-line serialisation would come back as unparseable and read as "incomplete".
        Assert.Equal(_catalog.CatalogName, generation.Manifest.CatalogName);

        var target = Path.Combine(_root, "restored-from-s3.ducklake");
        var restore = await CatalogRestore.RestoreAsync(
            _options, _catalog.CatalogName, generation: null, target, _catalog.DataPath,
            CancellationToken.None, CreateSecret);

        Assert.Equal(generation.Manifest.Tables.Count, restore.TablesRestored);

        var restored = new CatalogDescriptor(
            "restoreds3", CatalogMetadataKind.LocalFile, target, _catalog.DataPath);

        await using var pool = new DucklingPool(Options.Create(_options), NullLoggerFactory.Instance);
        var session = await pool.GetOrStartAsync(restored, CreateSecret, CancellationToken.None);

        Assert.Equal(liveRows, await ScalarAsync(session, "SELECT count(*) FROM people"));
        Assert.Equal("0", await ScalarAsync(session, "SELECT count(*) FROM people WHERE id < 11"));
    }

    [SkippableFact]
    public async Task Retention_reports_that_it_cannot_prune_a_bucket()
    {
        Skip.If(
            string.IsNullOrWhiteSpace(_endpoint),
            $"Set {EndpointVariable}, _KEY, _SECRET and _BUCKET to run object-store tests.");

        var duckling = await _pool!.GetOrStartAsync(_catalog, CreateSecret, CancellationToken.None);

        // DuckDB can write to an object store but cannot delete from one, so retention has to say
        // so. Silently reporting "0 pruned" would read as "retention ran and found nothing to do",
        // and the generations would pile up unnoticed.
        var backup = await duckling.InvokeAsync(
            ct => CatalogBackup.WriteAsync(duckling, _options, TimeProvider.System, ct),
            CancellationToken.None);

        Assert.True(backup.RetentionDeferred);
        Assert.Equal(0, backup.PrunedGenerations);
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

    private static async Task<string> ScalarAsync(Duckling duckling, string sql)
    {
        var result = await duckling.ExecuteQueryAsync(sql, CancellationToken.None);
        return Convert.ToString(result.Rows[0][0], CultureInfo.InvariantCulture) ?? string.Empty;
    }
}

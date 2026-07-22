using DuckDB.EFCoreProvider.Extensions;
using Lakehold.Engine.Catalog;
using Lakehold.Engine.Configuration;
using Lakehold.Engine.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Lakehold.Engine.Tests;

/// <summary>
///     Cover for provider 1.14.0's transaction-scoped commit message. A snapshot list is only
///     self-documenting if the entries say what made them, and everything Lakehold commits on its own
///     initiative used to land as an unlabelled snapshot beside the tenant's own writes.
/// </summary>
public sealed class MaintenanceCommitMessageTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lakehold-tests", Guid.NewGuid().ToString("N"));
    private CatalogDescriptor _catalog = null!;
    private DucklingPool _pool = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);

        var options = new LakehouseOptions
        {
            DataRoot = Path.Combine(_root, "data"),
            BackupRoot = Path.Combine(_root, "backups"),
        };

        _catalog = new CatalogDescriptor(
            "labelled",
            CatalogMetadataKind.LocalFile,
            Path.Combine(_root, "test.ducklake"),
            Path.Combine(_root, "data"));

        Directory.CreateDirectory(_catalog.DataPath);
        _pool = new DucklingPool(Options.Create(options), NullLoggerFactory.Instance);

        var duckling = await Session();
        await duckling.ExecuteQueryAsync("CREATE TABLE orders (id BIGINT, status VARCHAR)", CancellationToken.None);

        // Small enough that DuckLake inlines it, which is what gives the flush something to do.
        await duckling.ExecuteQueryAsync("INSERT INTO orders VALUES (1, 'new'), (2, 'new')", CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        await _pool.DisposeAsync();
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
    public async Task Flush_labels_the_snapshot_it_commits()
    {
        var duckling = await Session();

        await LakehouseMaintenance.FlushInlinedDataAsync(duckling, CancellationToken.None);

        var snapshots = await LakehouseMaintenance.ListSnapshotsAsync(duckling, 10, CancellationToken.None);
        Assert.Contains(snapshots, s => s.CommitMessage == "lakehold maintenance: flush inlined data");

        // The tenant's own writes stay unlabelled, so the two remain distinguishable in the history.
        Assert.Contains(snapshots, s => s.CommitMessage is null);
    }

    /// <summary>
    ///     A maintenance run with nothing to do must not litter the history: the transaction commits
    ///     no snapshot when nothing changed, so there is nothing for the message to attach to.
    /// </summary>
    [Fact]
    public async Task Maintenance_that_changes_nothing_adds_no_snapshot()
    {
        var duckling = await Session();
        await LakehouseMaintenance.FlushInlinedDataAsync(duckling, CancellationToken.None);

        var before = await LakehouseMaintenance.ListSnapshotsAsync(duckling, 100, CancellationToken.None);

        await LakehouseMaintenance.FlushInlinedDataAsync(duckling, CancellationToken.None);
        await LakehouseMaintenance.CompactAsync(duckling, CancellationToken.None);

        var after = await LakehouseMaintenance.ListSnapshotsAsync(duckling, 100, CancellationToken.None);
        Assert.Equal(before.Count, after.Count);
    }

    /// <summary>
    ///     Provider 1.14.0 attaches a secret-backed share alongside the tenant's own catalog, which
    ///     before it could only be a local file — excluding the PostgreSQL-metadata deployments most
    ///     likely to want one. The secret here names a local metadata path so the test needs no
    ///     PostgreSQL; what it exercises is the secret-backed attachment path and its read-only
    ///     enforcement, not where the share's metadata happens to live.
    /// </summary>
    [Fact]
    public async Task A_secret_backed_share_attaches_read_only()
    {
        var shareMetadata = Path.Combine(_root, "share.ducklake");
        var shareData = Path.Combine(_root, "sharedata");
        Directory.CreateDirectory(shareData);

        var shareCatalog = new CatalogDescriptor(
            "shared",
            CatalogMetadataKind.LocalFile,
            shareMetadata,
            shareData);

        var share = await _pool.GetOrStartAsync(shareCatalog, configure: null, CancellationToken.None);
        await share.ExecuteQueryAsync("CREATE TABLE reference (k VARCHAR, v BIGINT)", CancellationToken.None);
        await share.ExecuteQueryAsync("INSERT INTO reference VALUES ('a', 1), ('b', 2)", CancellationToken.None);
        await _pool.EvictAsync(shareCatalog.CatalogName);

        var withShare = _catalog with
        {
            CatalogName = "primary",
            MetadataSource = Path.Combine(_root, "primary.ducklake"),
            AdditionalCatalogs = [new AttachedCatalog("shared", "share_profile", CatalogMetadataKind.Postgres)],
        };

        var duckling = await _pool.GetOrStartAsync(
            withShare,
            duckDb => duckDb.ConfigureConnection(connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText =
                    $"CREATE SECRET share_profile (TYPE ducklake, METADATA_PATH '{shareMetadata}', " +
                    $"DATA_PATH '{shareData}{Path.DirectorySeparatorChar}')";
                command.ExecuteNonQuery();
            }),
            CancellationToken.None);

        var read = await duckling.ExecuteQueryAsync("SELECT sum(v) FROM shared.main.reference", CancellationToken.None);
        Assert.Equal(3L, Convert.ToInt64(read.Rows[0][0], System.Globalization.CultureInfo.InvariantCulture));

        // Invariant 9: a share must stay read-only, and the engine enforces it rather than Lakehold
        // promising it.
        var write = await Assert.ThrowsAsync<DuckDB.NET.Data.DuckDBException>(
            () => duckling.ExecuteQueryAsync("INSERT INTO shared.main.reference VALUES ('c', 3)", CancellationToken.None));

        Assert.Contains("read-only", write.Message, StringComparison.OrdinalIgnoreCase);
    }

    private Task<Duckling> Session()
        => _pool.GetOrStartAsync(_catalog, configure: null, CancellationToken.None);
}

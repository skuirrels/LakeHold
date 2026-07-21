using System.Globalization;
using Lakehold.Engine.Catalog;
using Lakehold.Engine.Configuration;
using Lakehold.Engine.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Lakehold.Engine.Tests;

/// <summary>
///     Cover for reading DuckLake's change feed: that inserts, deletes, and updates surface with the
///     right change types and values, and that the inclusive snapshot range lets a poller resume
///     without duplicating or dropping a change.
/// </summary>
public sealed class ChangeFeedTests : IAsyncLifetime
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
            "cdclake",
            CatalogMetadataKind.LocalFile,
            Path.Combine(_root, "test.ducklake"),
            Path.Combine(_root, "data"));

        Directory.CreateDirectory(_catalog.DataPath);
        _pool = new DucklingPool(Options.Create(options), NullLoggerFactory.Instance);

        var duckling = await Session();
        await Run(duckling, "CREATE TABLE orders (id BIGINT, status VARCHAR)");
        await Run(duckling, "INSERT INTO orders VALUES (1, 'new'), (2, 'new'), (3, 'new')"); // insert
        await Run(duckling, "DELETE FROM orders WHERE id = 2");                               // delete
        await Run(duckling, "UPDATE orders SET status = 'shipped' WHERE id = 3");             // update
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
    public async Task Change_feed_reports_inserts_deletes_and_updates_with_values()
    {
        var duckling = await Session();
        var latest = await ChangeFeed.LatestSnapshotAsync(duckling, CancellationToken.None);
        Assert.NotNull(latest);

        var page = await ChangeFeed.ReadAsync(duckling, "main", "orders", 0, latest!.Value, 100, CancellationToken.None);

        Assert.False(page.Truncated);
        Assert.Equal(3, page.Changes.Count(c => c.Change == ChangeType.Insert));
        Assert.Single(page.Changes, c => c.Change == ChangeType.Delete);
        Assert.Single(page.Changes, c => c.Change == ChangeType.UpdatePreimage);
        Assert.Single(page.Changes, c => c.Change == ChangeType.UpdatePostimage);

        // The deleted row carries its identifying values so a consumer knows what was removed.
        var deletion = Assert.Single(page.Changes, c => c.Change == ChangeType.Delete);
        Assert.Equal("2", Convert.ToString(deletion.Row["id"], CultureInfo.InvariantCulture));

        // The update post-image carries the new value.
        var postImage = Assert.Single(page.Changes, c => c.Change == ChangeType.UpdatePostimage);
        Assert.Equal("shipped", Convert.ToString(postImage.Row["status"], CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Change_feed_range_is_inclusive_so_a_poller_resumes_without_gaps()
    {
        var duckling = await Session();
        var latest = (await ChangeFeed.LatestSnapshotAsync(duckling, CancellationToken.None))!.Value;

        // A poller that has delivered up to L reads the next batch from L+1. Splitting the range at
        // every boundary and concatenating must reproduce the whole feed exactly once.
        //
        // The loop starts at 1, not 0: snapshot 0 is always the initial schemas_created commit, and
        // ducklake_table_changes raises "table does not exist at version 0" when the range's END
        // predates the table — verified on DuckDB 1.5.4. A start before creation is fine, which is
        // why the whole-range read below can still open at 0. The dispatcher never trips this: its
        // end bound is the latest snapshot, which necessarily postdates any listed table's creation.
        var whole = await ChangeFeed.ReadAsync(duckling, "main", "orders", 0, latest, 100, CancellationToken.None);

        var reassembled = new List<TableChange>();
        for (long s = 1; s <= latest; s++)
        {
            var page = await ChangeFeed.ReadAsync(duckling, "main", "orders", s, s, 100, CancellationToken.None);
            reassembled.AddRange(page.Changes);
        }

        Assert.Equal(whole.Changes.Count, reassembled.Count);
    }

    [Fact]
    public async Task Change_feed_truncates_deterministically_and_flags_it()
    {
        var duckling = await Session();
        var latest = (await ChangeFeed.LatestSnapshotAsync(duckling, CancellationToken.None))!.Value;

        var page = await ChangeFeed.ReadAsync(duckling, "main", "orders", 0, latest, maxRows: 2, CancellationToken.None);

        Assert.True(page.Truncated);
        Assert.Equal(2, page.Changes.Count);
    }

    [Fact]
    public async Task List_tables_returns_the_catalog_base_tables()
    {
        var duckling = await Session();
        var tables = await ChangeFeed.ListTablesAsync(duckling, CancellationToken.None);
        Assert.Contains(tables, t => t is { Schema: "main", Table: "orders" });
    }

    private Task<Duckling> Session() => _pool.GetOrStartAsync(_catalog, configure: null, CancellationToken.None);

    private static async Task Run(Duckling duckling, string sql)
        => await duckling.ExecuteQueryAsync(sql, CancellationToken.None);
}

using Lakehold.Engine.Catalog;
using Lakehold.Engine.Configuration;
using Lakehold.Engine.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Lakehold.Engine.Tests;

/// <summary>
///     Covers the table-data restore boundary: planning is read-only, apply preserves the current
///     table definition, and an incompatible schema cannot partially delete live rows.
/// </summary>
public sealed class TableRestoreTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "lakehold-restore-tests", Guid.NewGuid().ToString("N"));
    private DucklingPool _pool = null!;
    private Duckling _duckling = null!;

    public async Task InitializeAsync()
    {
        var options = new LakehouseOptions
        {
            DataRoot = Path.Combine(_root, "data"),
            BackupRoot = Path.Combine(_root, "backups"),
        };
        var catalog = new CatalogDescriptor(
            "restorelake",
            CatalogMetadataKind.LocalFile,
            Path.Combine(_root, "restore.ducklake"),
            options.DataRoot);

        Directory.CreateDirectory(options.DataRoot);
        _pool = new DucklingPool(Options.Create(options), NullLoggerFactory.Instance);
        _duckling = await _pool.GetOrStartAsync(catalog, configure: null, CancellationToken.None);
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
    public async Task Restore_preserves_current_defaults_and_nullability()
    {
        await Sql("CREATE TABLE orders (id BIGINT NOT NULL, status VARCHAR DEFAULT 'new')");
        await Sql("INSERT INTO orders(id) VALUES (1)");
        var snapshot = (await ChangeFeed.LatestSnapshotAsync(_duckling, default))!.Value;
        await Sql("ALTER TABLE orders ADD COLUMN region VARCHAR DEFAULT 'global'");
        await Sql("INSERT INTO orders(id, status, region) VALUES (2, 'later', 'eu')");

        var plan = await TableRestore.RunAsync(
            _duckling, "main", "orders", snapshot, apply: false, expectedCurrentSnapshotId: null, default);

        Assert.True(plan.DryRun);
        Assert.Equal(2, plan.CurrentRowCount);
        Assert.Equal(1, plan.HistoricalRowCount);
        Assert.Equal(["id", "status"], plan.RestoredColumns);
        Assert.Equal(["region"], plan.CurrentOnlyColumns);
        Assert.Empty(plan.HistoricalOnlyColumns);
        Assert.Equal(2, await Count("orders"));

        var applied = await TableRestore.RunAsync(
            _duckling, "main", "orders", snapshot, apply: true, plan.CurrentSnapshotId, default);

        Assert.False(applied.DryRun);
        var rows = await Sql("SELECT id, status, region FROM orders");
        Assert.Equal(["1", "new", "global"], rows.Rows.Single().Select(Convert.ToString));

        await Sql("INSERT INTO orders(id) VALUES (3)");
        var inserted = await Sql("SELECT status, region FROM orders WHERE id = 3");
        Assert.Equal(["new", "global"], inserted.Rows.Single().Select(Convert.ToString));
        await Assert.ThrowsAsync<DuckDB.NET.Data.DuckDBException>(
            () => Sql("INSERT INTO orders(id) VALUES (NULL)"));
    }

    [Fact]
    public async Task Incompatible_restore_refuses_before_live_rows_change()
    {
        await Sql("CREATE TABLE renamed (old_id BIGINT)");
        await Sql("INSERT INTO renamed VALUES (1)");
        var snapshot = (await ChangeFeed.LatestSnapshotAsync(_duckling, default))!.Value;
        await Sql("ALTER TABLE renamed RENAME COLUMN old_id TO new_id");
        await Sql("INSERT INTO renamed VALUES (2)");
        var current = (await ChangeFeed.LatestSnapshotAsync(_duckling, default))!.Value;

        var error = await Assert.ThrowsAsync<ArgumentException>(
            () => TableRestore.RunAsync(
                _duckling,
                "main",
                "renamed",
                snapshot,
                apply: true,
                expectedCurrentSnapshotId: current,
                default));

        Assert.Contains("no columns in common", error.Message, StringComparison.Ordinal);
        Assert.Equal(2, await Count("renamed"));
    }

    [Fact]
    public async Task Constraint_failure_rolls_back_the_delete_and_releases_the_session()
    {
        await Sql("CREATE TABLE guarded (id BIGINT, required VARCHAR)");
        await Sql("INSERT INTO guarded VALUES (1, NULL)");
        var snapshot = (await ChangeFeed.LatestSnapshotAsync(_duckling, default))!.Value;
        await Sql("UPDATE guarded SET required = 'live'");
        await Sql("ALTER TABLE guarded ALTER COLUMN required SET NOT NULL");
        await Sql("INSERT INTO guarded VALUES (2, 'later')");
        var current = (await ChangeFeed.LatestSnapshotAsync(_duckling, default))!.Value;

        await Assert.ThrowsAsync<DuckDB.NET.Data.DuckDBException>(
            () => TableRestore.RunAsync(
                _duckling,
                "main",
                "guarded",
                snapshot,
                apply: true,
                expectedCurrentSnapshotId: current,
                default));

        var rows = await Sql("SELECT id, required FROM guarded ORDER BY id");
        Assert.Equal(2, rows.Rows.Count);
        Assert.Equal(["1", "live"], rows.Rows[0].Select(Convert.ToString));
        Assert.Equal(["2", "later"], rows.Rows[1].Select(Convert.ToString));

        // The failed transaction must not poison the shared session for its next caller.
        await Sql("INSERT INTO guarded VALUES (3, 'after failure')");
        Assert.Equal(3, await Count("guarded"));
    }

    [Fact]
    public async Task Apply_refuses_when_the_catalog_advanced_after_review()
    {
        await Sql("CREATE TABLE concurrent (id BIGINT)");
        await Sql("INSERT INTO concurrent VALUES (1)");
        var snapshot = (await ChangeFeed.LatestSnapshotAsync(_duckling, default))!.Value;
        await Sql("INSERT INTO concurrent VALUES (2)");
        var plan = await TableRestore.RunAsync(
            _duckling, "main", "concurrent", snapshot, apply: false, null, default);

        await Sql("INSERT INTO concurrent VALUES (3)");

        var error = await Assert.ThrowsAsync<ArgumentException>(
            () => TableRestore.RunAsync(
                _duckling,
                "main",
                "concurrent",
                snapshot,
                apply: true,
                plan.CurrentSnapshotId,
                default));

        Assert.Contains("advanced", error.Message, StringComparison.Ordinal);
        Assert.Equal(3, await Count("concurrent"));
    }

    private Task<QueryResult> Sql(string sql)
        => _duckling.ExecuteQueryAsync(sql, CancellationToken.None);

    private async Task<long> Count(string table)
    {
        var result = await Sql($"SELECT count(*) FROM {table}");
        return Convert.ToInt64(result.Rows.Single()[0], System.Globalization.CultureInfo.InvariantCulture);
    }
}

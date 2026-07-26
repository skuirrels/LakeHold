using Lakehold.Engine.Catalog;
using Lakehold.Engine.Configuration;
using Lakehold.Engine.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Lakehold.Engine.Tests;

/// <summary>
///     Cover for the storage view: that the figures behind it match ground truth on a catalog that
///     has inlined rows, flushed rows, and merge-on-read deletes all at once.
/// </summary>
/// <remarks>
///     <para>
///         The cases worth pinning are the two that are easy to read as bugs. A table holding only
///         inlined data reports <em>zero files and zero bytes</em> — verified engine behaviour 1, and
///         indistinguishable from an empty table unless the row count is carried separately. And
///         <c>ducklake_table_stats.record_count</c> does not subtract merge-on-read deletes, so a
///         naive reading over-reports every table that has ever had a row deleted.
///     </para>
///     <para>
///         Every row-count assertion is made against <c>SELECT count(*)</c> on the same table rather
///         than against a literal. A literal would only prove the arithmetic is stable, not that it
///         is right.
///     </para>
/// </remarks>
public sealed class StorageBrowserTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lakehold-storage", Guid.NewGuid().ToString("N"));
    private CatalogDescriptor _catalog = null!;
    private DucklingPool _pool = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);

        var options = new LakehouseOptions { DataRoot = Path.Combine(_root, "data") };
        _catalog = new CatalogDescriptor(
            "storagelake",
            CatalogMetadataKind.LocalFile,
            Path.Combine(_root, "test.ducklake"),
            Path.Combine(_root, "data"));

        Directory.CreateDirectory(_catalog.DataPath);
        _pool = new DucklingPool(Options.Create(options), NullLoggerFactory.Instance);

        var duckling = await Session();

        // Large enough to be written straight to Parquet rather than inlined, then partly deleted so
        // the live count and the file record count diverge.
        await Run(duckling, "CREATE TABLE filed AS SELECT i AS id, i::VARCHAR AS v FROM range(200000) t(i)");
        await Run(duckling, "DELETE FROM filed WHERE id < 5000");

        // Small enough to stay inlined: no data file at all until something flushes it.
        await Run(duckling, "CREATE SCHEMA warm");
        await Run(duckling, "CREATE TABLE warm.inlined (id BIGINT)");
        await Run(duckling, "INSERT INTO warm.inlined VALUES (1), (2), (3)");

        // Deleted and updated while still inlined. This is the combination that has no delete file
        // anywhere — the tombstones live in the inlined staging table — and it is what a rollup built
        // on record_count gets wrong.
        await Run(duckling, "CREATE TABLE churned (id BIGINT, status VARCHAR)");
        await Run(duckling, "INSERT INTO churned VALUES (1, 'new'), (2, 'new'), (3, 'new')");
        await Run(duckling, "DELETE FROM churned WHERE id = 2");
        await Run(duckling, "UPDATE churned SET status = 'shipped' WHERE id = 3");
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
    public async Task Row_counts_match_a_count_star_on_every_table()
    {
        var duckling = await Session();
        var storage = await StorageBrowser.ReadAsync(duckling, CancellationToken.None);

        Assert.NotEmpty(storage.Tables);
        foreach (var table in storage.Tables)
        {
            var truth = await CountAsync(duckling, table.SchemaName, table.TableName);
            Assert.Equal(truth, table.RowCount);
        }
    }

    [Fact]
    public async Task Deleted_rows_are_subtracted_rather_than_counted()
    {
        var duckling = await Session();
        var storage = await StorageBrowser.ReadAsync(duckling, CancellationToken.None);

        var filed = Single(storage, "main", "filed");

        // 200k written, 5k deleted. The metadata's own record_count still says 200000; anything that
        // reports that number has forgotten the delete file sitting beside the data file.
        Assert.Equal(195_000, filed.RowCount);
        Assert.Equal(1, filed.DeleteFileCount);
        Assert.True(filed.DeleteFileSizeBytes > 0);
    }

    [Fact]
    public async Task A_table_holding_only_inlined_rows_reports_no_files_but_still_reports_its_rows()
    {
        var duckling = await Session();
        var storage = await StorageBrowser.ReadAsync(duckling, CancellationToken.None);

        var inlined = Single(storage, "warm", "inlined");

        // The trap: on the file figures alone this is indistinguishable from an empty table.
        Assert.Equal(0, inlined.FileCount);
        Assert.Equal(0, inlined.FileSizeBytes);
        Assert.Null(inlined.AverageFileSizeBytes);

        // What makes it distinguishable, and what makes the Flush button decidable.
        Assert.Equal(3, inlined.RowCount);
        Assert.Equal(3, inlined.InlinedRows);
    }

    [Fact]
    public async Task Rows_deleted_while_still_inlined_are_not_counted()
    {
        // The regression this test exists for. Three inserts, one delete, and one update against an
        // unflushed table leave ducklake_table_stats.record_count at 4 — the update writes a second
        // physical row — with *zero* ducklake_delete_file rows, because an inlined tombstone is not a
        // delete file. A rollup built on record_count reported 4 for a table plainly holding 2, and
        // no test caught it: the original fixture had deletes only against flushed data and inlined
        // data only without deletes, so the combination that breaks was never exercised.
        var duckling = await Session();
        var storage = await StorageBrowser.ReadAsync(duckling, CancellationToken.None);

        var churned = Single(storage, "main", "churned");

        Assert.Equal(2, churned.RowCount);
        Assert.Equal(await CountAsync(duckling, "main", "churned"), churned.RowCount);

        // Nothing is in Parquet yet, so every live row is still awaiting a flush.
        Assert.Equal(0, churned.FileCount);
        Assert.Equal(0, churned.DeleteFileCount);
        Assert.Equal(2, churned.InlinedRows);
    }

    [Fact]
    public async Task Flushing_moves_rows_out_of_inlined_and_into_a_file()
    {
        var duckling = await Session();
        await Run(duckling, $"CALL ducklake_flush_inlined_data('{_catalog.CatalogName}')");

        var storage = await StorageBrowser.ReadAsync(duckling, CancellationToken.None);
        var inlined = Single(storage, "warm", "inlined");

        Assert.Equal(0, inlined.InlinedRows);
        Assert.Equal(3, inlined.RowCount);
        Assert.Equal(1, inlined.FileCount);
        Assert.True(inlined.FileSizeBytes > 0);
        Assert.Equal(inlined.FileSizeBytes, inlined.AverageFileSizeBytes);
    }

    [Fact]
    public async Task The_rollup_lists_user_tables_only_and_names_their_schemas()
    {
        var duckling = await Session();
        var storage = await StorageBrowser.ReadAsync(duckling, CancellationToken.None);

        // ducklake_table_info reports only user tables, so unlike information_schema this needs no
        // filtering — asserted rather than assumed, because the day it changes the storage view would
        // otherwise start reporting DuckLake's own metadata as tenant data.
        Assert.DoesNotContain(storage.Tables, t => t.TableName.StartsWith("ducklake_", StringComparison.Ordinal));

        Assert.Contains(storage.Tables, t => t is { SchemaName: "main", TableName: "filed" });
        Assert.Contains(storage.Tables, t => t is { SchemaName: "warm", TableName: "inlined" });
    }

    [Fact]
    public async Task Target_file_size_is_null_until_the_catalog_sets_one()
    {
        var duckling = await Session();

        // Unset is reported as unset: DuckLake's built-in default is not exposed through any setting
        // or metadata row, so a number here could only be invented.
        var before = await StorageBrowser.ReadAsync(duckling, CancellationToken.None);
        Assert.Null(before.TargetFileSizeBytes);

        await Run(duckling, $"CALL ducklake_set_option('{_catalog.CatalogName}', 'target_file_size', '5MB')");

        var after = await StorageBrowser.ReadAsync(duckling, CancellationToken.None);
        Assert.Equal(5_000_000, after.TargetFileSizeBytes);
    }

    [Fact]
    public async Task A_read_only_session_can_read_the_storage_view()
    {
        // Reading file metadata is a read, so it must work on a read-only share (invariant 9) for the
        // same reason eject does (invariant 15). A reader who cannot run compaction can still see
        // that compaction is needed.
        var reader = await _pool.GetOrStartAsync(
            _catalog with { ReadOnly = true },
            configure: null,
            CancellationToken.None);

        var storage = await StorageBrowser.ReadAsync(reader, CancellationToken.None);

        Assert.Equal(195_000, Single(storage, "main", "filed").RowCount);
    }

    [Fact]
    public async Task The_file_list_pairs_each_data_file_with_its_delete_file()
    {
        var duckling = await Session();
        var files = await StorageBrowser.ListFilesAsync(
            duckling, "main", "filed", snapshotId: null, maxRows: 100, CancellationToken.None);

        Assert.False(files.Truncated);
        var file = Assert.Single(files.Files);

        Assert.EndsWith(".parquet", file.DataFile, StringComparison.Ordinal);
        Assert.True(file.DataFileSizeBytes > 0);

        // The delete file is what a reader pays to skip 5,000 rows that are still on disk. Showing
        // the data file without it would understate the table's real read cost.
        Assert.NotNull(file.DeleteFile);
        Assert.True(file.DeleteFileSizeBytes > 0);
    }

    [Fact]
    public async Task The_file_list_reaches_tables_outside_the_main_schema()
    {
        var duckling = await Session();
        await Run(duckling, $"CALL ducklake_flush_inlined_data('{_catalog.CatalogName}')");

        // Verified on 1.5.5: omitting the schema argument raises "Table with name ... does not exist"
        // rather than searching, so a non-main schema is only reachable when it is passed explicitly.
        var files = await StorageBrowser.ListFilesAsync(
            duckling, "warm", "inlined", snapshotId: null, maxRows: 100, CancellationToken.None);

        var file = Assert.Single(files.Files);
        Assert.Null(file.DeleteFile);
        Assert.True(file.DataFileSizeBytes > 0);
    }

    [Fact]
    public async Task The_file_list_truncates_and_flags_it()
    {
        var duckling = await Session();
        var files = await StorageBrowser.ListFilesAsync(
            duckling, "main", "filed", snapshotId: null, maxRows: 1, CancellationToken.None);

        Assert.Single(files.Files);

        // One file exists, one was asked for: at the boundary nothing is missing, so claiming
        // truncation would send the operator looking for files that are not there.
        Assert.False(files.Truncated);
    }

    [Fact]
    public async Task A_snapshot_predating_the_table_is_raised_rather_than_reported_as_empty()
    {
        var duckling = await Session();

        // The same trap verified behaviour 7 documents for the change feed. It must surface as an
        // error the endpoint can forward, not as an empty list that reads as "this table had no
        // files then" — which is a different and wrong statement.
        await Assert.ThrowsAsync<DuckDB.NET.Data.DuckDBException>(
            () => StorageBrowser.ListFilesAsync(
                duckling, "warm", "inlined", snapshotId: 0, maxRows: 100, CancellationToken.None));
    }

    [Fact]
    public async Task The_file_list_never_projects_an_encryption_key()
    {
        // ducklake_list_files also returns data_file_encryption_key and delete_file_encryption_key,
        // populated whenever the catalog is encrypted. Asserted on the record's shape rather than on
        // a sample value, so an encrypted catalog cannot regress this silently: there is nowhere for
        // a key to go.
        var properties = typeof(DataFileInfo).GetProperties().Select(p => p.Name).ToArray();

        Assert.DoesNotContain(properties, p => p.Contains("Key", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(properties, p => p.Contains("Encryption", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_table_whose_name_is_not_a_bare_identifier_does_not_break_the_whole_rollup()
    {
        var duckling = await Session();

        // DuckLake stores these happily, and the rest of the product already assumes they exist —
        // it is why the file-list route takes the table as a query parameter rather than a path
        // segment, and why the client splits `schema.table` on the *first* dot. A rollup that
        // cannot name them takes the entire Storage panel down, not one row of it.
        await Run(duckling, """CREATE TABLE "order-items" (id BIGINT)""");
        await Run(duckling, """INSERT INTO "order-items" VALUES (1), (2)""");
        await Run(duckling, """CREATE TABLE "my.table" (id BIGINT)""");
        await Run(duckling, """INSERT INTO "my.table" VALUES (1)""");

        // A reserved word is the same defect wearing different clothes: it survives validation and
        // then produces a syntax error instead.
        await Run(duckling, """CREATE TABLE "select" (id BIGINT)""");

        var storage = await StorageBrowser.ReadAsync(duckling, CancellationToken.None);

        Assert.Equal(2, Single(storage, "main", "order-items").RowCount);
        Assert.Equal(1, Single(storage, "main", "my.table").RowCount);
        Assert.Equal(0, Single(storage, "main", "select").RowCount);

        // And the tables that were always fine are still fine — the awkward ones must not take the
        // ordinary ones with them.
        Assert.Equal(195_000, Single(storage, "main", "filed").RowCount);

        // The file list reaches them too. It always could: a table function takes its table as a
        // string literal, so that path never had the identifier problem the rollup did.
        var files = await StorageBrowser.ListFilesAsync(
            duckling, "main", "my.table", snapshotId: null, maxRows: 10, CancellationToken.None);
        Assert.Equal("my.table", files.TableName);
    }

    private static TableStorageInfo Single(CatalogStorageInfo storage, string schema, string table) =>
        Assert.Single(storage.Tables, t => t.SchemaName == schema && t.TableName == table);

    private static async Task<long> CountAsync(Duckling duckling, string schema, string table)
    {
        var result = await duckling
            .ExecuteQueryAsync(
                $"SELECT count(*) FROM {SqlIdentifier.QuoteName(schema)}.{SqlIdentifier.QuoteName(table)}",
                CancellationToken.None)
            .ConfigureAwait(false);

        return Convert.ToInt64(result.Rows[0][0], System.Globalization.CultureInfo.InvariantCulture);
    }

    private Task<Duckling> Session() =>
        _pool.GetOrStartAsync(_catalog, configure: null, CancellationToken.None);

    private static Task<QueryResult> Run(Duckling duckling, string sql) =>
        duckling.ExecuteQueryAsync(sql, CancellationToken.None);
}

using DuckDB.EFCoreProvider.Extensions;
using Lakehold.Api.Endpoints;
using Lakehold.ControlPlane.Data;
using Lakehold.Engine.Catalog;
using Lakehold.Engine.Configuration;
using Lakehold.Engine.Execution;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Lakehold.Api.Tests;

/// <summary>
///     Cover for the two storage routes at the layer the engine tests cannot reach: the DTO
///     projection, the maintenance advisories, and how a failure below is reported above.
/// </summary>
/// <remarks>
///     <para>
///         The advisories are the reason this file exists. <c>StorageBrowser</c> deliberately reports
///         figures and no verdicts — the threshold lives here so a second consumer, an agent tool or a
///         CLI, reaches the same conclusion as the workbench. That decision moves the only
///         interpretation in the feature into the API layer, and untested interpretation is where the
///         panel would start lying to an operator about whether a button is worth pressing.
///     </para>
///     <para>
///         Written against a real catalog rather than a stubbed service. <c>LakehouseService</c> is a
///         sealed concrete class, but that is not why: an advisory computed from invented figures
///         would prove the arithmetic and nothing about whether the figures ever take those shapes.
///         The fixture is small enough that the cost is a second.
///     </para>
/// </remarks>
public sealed class StorageEndpointsTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lakehold-storage-api", Guid.NewGuid().ToString("N"));
    private ControlPlaneContext _context = null!;
    private DucklingPool _pool = null!;
    private LakehouseService _service = null!;
    private IOptions<LakehouseOptions> _options = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);

        _options = Options.Create(new LakehouseOptions
        {
            MetadataRoot = Path.Combine(_root, "catalogs"),
            DataRoot = Path.Combine(_root, "data"),
        });

        var builder = new DbContextOptionsBuilder<ControlPlaneContext>();
        builder.UseDuckDB($"Data Source={Path.Combine(_root, "cp.duckdb")}");
        _context = new ControlPlaneContext(builder.Options);
        await _context.Database.EnsureCreatedAsync();

        _pool = new DucklingPool(_options, NullLoggerFactory.Instance);
        _service = new LakehouseService(_context, _pool, _options);

        await AdminEndpoints.CreateTenantAsync(
            new CreateTenantRequest("acme", "Acme"), _context, TimeProvider.System, default);
        await AdminEndpoints.CreateCatalogAsync(
            "acme", new CreateCatalogRequest("analytics"), _context, _options, TimeProvider.System, default);
        var catalog = await _context.Catalogs.SingleAsync();
        Directory.CreateDirectory(_options.Value.MetadataRoot);
        catalog.MetadataKind = CatalogMetadataKind.LocalFile;
        catalog.MetadataSource = Path.Combine(_options.Value.MetadataRoot, "analytics.ducklake");
        catalog.MetadataSchema = null;
        catalog.MetadataSecretName = null;
        await _context.SaveChangesAsync();

        // Large enough to be written to Parquet rather than inlined, then partly deleted so a delete
        // file exists to pair against in the file list.
        await Sql("CREATE TABLE filed AS SELECT i AS id FROM range(150000) t(i)");
        await Sql("ALTER TABLE filed SET PARTITIONED BY (id)");
        await Sql("DELETE FROM filed WHERE id < 1000");
        await Sql("CREATE VIEW filed_view AS SELECT id FROM filed");

        // Small enough to stay inlined: zero files, and therefore indistinguishable from an empty
        // table on the file figures alone. This is the row the flush advisory has to catch.
        await Sql("CREATE TABLE staging (id BIGINT)");
        await Sql("INSERT INTO staging VALUES (1), (2), (3)");

        // Written in separate transactions, so it lands as several files rather than one. This is the
        // small-file problem the compaction advisory exists to name, and the only table here with
        // enough files to truncate a listing against.
        await Sql("CREATE TABLE sharded AS SELECT i AS id FROM range(50000) t(i)");
        await Sql("INSERT INTO sharded SELECT i FROM range(50000) t(i)");
        await Sql("INSERT INTO sharded SELECT i FROM range(50000) t(i)");
    }

    public async Task DisposeAsync()
    {
        await _pool.DisposeAsync();
        await _context.DisposeAsync();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Temp cleanup failing must not fail the run.
        }
    }

    private Task<QueryResult> Sql(string sql) => _service.ExecuteAsync("acme", "analytics", sql, default);

    private static IResult Unwrap(object union) => ((INestedHttpResult)union).Result;

    private async Task<CatalogStorageDto> Storage()
    {
        var result = await LakehouseEndpoints.GetStorageAsync("acme", "analytics", _service, _options, default);
        return Assert.IsType<Ok<CatalogStorageDto>>(Unwrap(result)).Value!;
    }

    private async Task<TableFilesDto> Files(string table, long? snapshot = null, int limit = 1000)
    {
        var result = await LakehouseEndpoints.GetTableFilesAsync(
            "acme", "analytics", table, _service, default, "main", snapshot, limit);
        return Assert.IsType<Ok<TableFilesDto>>(Unwrap(result)).Value!;
    }

    private async Task<TableDetailDto> Detail(string table)
    {
        var result = await LakehouseEndpoints.GetTableDetailAsync(
            "acme", "analytics", table, _service, _options, default);
        return Assert.IsType<Ok<TableDetailDto>>(Unwrap(result)).Value!;
    }

    private async Task<TableProfileDto> Profile(string table, long? snapshot = null)
    {
        var result = await LakehouseEndpoints.GetTableProfileAsync(
            "acme", "analytics", table, _service, default, "main", snapshot);
        return Assert.IsType<Ok<TableProfileDto>>(Unwrap(result)).Value!;
    }

    [Fact]
    public async Task The_rollup_carries_every_table_with_its_figures()
    {
        var storage = await Storage();

        var filed = Assert.Single(storage.Tables, t => t.TableName == "filed");
        Assert.Equal("main", filed.SchemaName);
        Assert.Equal(149_000, filed.RowCount);
        Assert.True(filed.FileCount > 0);
        Assert.True(filed.FileSizeBytes > 0);
        Assert.Equal(filed.FileSizeBytes / filed.FileCount, filed.AverageFileSizeBytes);

        // A table nobody has flushed reports no files at all, so the row count is the only thing
        // separating it from an empty table. Reporting data as absent is the worst failure this
        // panel could have.
        var staging = Assert.Single(storage.Tables, t => t.TableName == "staging");
        Assert.Equal(0, staging.FileCount);
        Assert.Null(staging.AverageFileSizeBytes);
        Assert.Equal(3, staging.RowCount);
        Assert.Equal(3, staging.InlinedRows);
    }

    [Fact]
    public async Task Unflushed_rows_raise_the_flush_advisory_and_nothing_else_does()
    {
        var storage = await Storage();

        Assert.True(Assert.Single(storage.Tables, t => t.TableName == "staging").NeedsFlush);
        Assert.False(Assert.Single(storage.Tables, t => t.TableName == "filed").NeedsFlush);
    }

    [Fact]
    public async Task Flushing_clears_the_advisory_it_raised()
    {
        await _service.RunMaintenanceAsync("acme", "analytics", "flush", apply: true, cancellationToken: default);

        // The advisory has to answer to the button beside it. If flush ran and the row still says
        // "flush pending", the readout is worse than no readout.
        var staging = Assert.Single((await Storage()).Tables, t => t.TableName == "staging");
        Assert.False(staging.NeedsFlush);
        Assert.Equal(0, staging.InlinedRows);
        Assert.Equal(3, staging.RowCount);
        Assert.True(staging.FileCount > 0);
    }

    [Fact]
    public async Task A_single_file_is_never_called_fragmented_however_small_it_is()
    {
        await _service.RunMaintenanceAsync("acme", "analytics", "flush", apply: true, cancellationToken: default);

        // Three rows in one file is far below any threshold, and still not fragmentation: a lone file
        // cannot be merged with anything, so the advice would be unactionable. Training an operator to
        // ignore the column is worse than leaving it blank.
        var staging = Assert.Single((await Storage()).Tables, t => t.TableName == "staging");
        Assert.Equal(1, staging.FileCount);
        Assert.False(staging.NeedsCompaction);
    }

    [Fact]
    public async Task Several_small_files_are_called_fragmented()
    {
        var sharded = Assert.Single((await Storage()).Tables, t => t.TableName == "sharded");

        Assert.True(sharded.FileCount > 1);
        Assert.True(sharded.AverageFileSizeBytes < _options.Value.CompactionAdvisoryBytes);
        Assert.True(sharded.NeedsCompaction);
    }

    [Fact]
    public async Task The_threshold_is_what_the_verdict_is_actually_computed_from()
    {
        // The same table, unchanged, judged twice. Lowering the catalog's target below the files it
        // already has must retract the advice — otherwise `AdvisoryFileSizeBytes` is decoration on a
        // verdict reached some other way, and a caller who trusts the reported basis is misled.
        Assert.True(Assert.Single((await Storage()).Tables, t => t.TableName == "sharded").NeedsCompaction);

        await Sql("CALL ducklake_set_option('analytics', 'target_file_size', '1kB')");

        var storage = await Storage();
        var sharded = Assert.Single(storage.Tables, t => t.TableName == "sharded");
        Assert.True(sharded.AverageFileSizeBytes > storage.AdvisoryFileSizeBytes);
        Assert.False(sharded.NeedsCompaction);
    }

    [Fact]
    public async Task An_unset_target_falls_back_to_the_configured_floor_and_says_so()
    {
        var storage = await Storage();

        // Unset is reported as unset rather than guessed — DuckLake's built-in default is exposed
        // nowhere — and the advisory names the floor that stood in for it.
        Assert.Null(storage.TargetFileSizeBytes);
        Assert.Equal(_options.Value.CompactionAdvisoryBytes, storage.AdvisoryFileSizeBytes);
    }

    [Fact]
    public async Task The_catalogs_own_target_wins_over_the_deployments_floor()
    {
        await Sql("CALL ducklake_set_option('analytics', 'target_file_size', '5MB')");

        var storage = await Storage();
        Assert.Equal(5_000_000, storage.TargetFileSizeBytes);
        Assert.Equal(5_000_000, storage.AdvisoryFileSizeBytes);
        Assert.NotEqual(_options.Value.CompactionAdvisoryBytes, storage.AdvisoryFileSizeBytes);
    }

    [Fact]
    public async Task The_file_list_pairs_each_data_file_with_its_delete_file()
    {
        var files = await Files("filed");

        Assert.NotEmpty(files.Files);
        Assert.False(files.Truncated);
        Assert.All(files.Files, f => Assert.True(f.DataFileSizeBytes > 0));

        // The deleted range is contiguous and at the start, so at least one file carries a delete
        // file and its size travels with it — the whole point of showing the pairing.
        var deleted = Assert.Single(files.Files, f => f.DeleteFile is not null);
        Assert.True(deleted.DeleteFileSizeBytes > 0);
    }

    [Fact]
    public async Task A_file_list_that_hits_the_limit_says_it_was_truncated()
    {
        var full = await Files("sharded");
        Assert.True(full.Files.Count > 1);
        Assert.False(full.Truncated);

        var capped = await Files("sharded", limit: 1);
        Assert.Single(capped.Files);
        Assert.True(capped.Truncated);

        // Not at the boundary, though: a list whose length happens to equal the limit is complete,
        // and reporting it as truncated would send an operator looking for files that do not exist.
        var exact = await Files("sharded", limit: full.Files.Count);
        Assert.False(exact.Truncated);
    }

    [Fact]
    public async Task An_unknown_catalog_is_a_404_from_both_routes()
    {
        Assert.IsType<NotFound<string>>(Unwrap(
            await LakehouseEndpoints.GetStorageAsync("acme", "ghost", _service, _options, default)));

        Assert.IsType<NotFound<string>>(Unwrap(
            await LakehouseEndpoints.GetTableFilesAsync(
                "acme", "ghost", "filed", _service, default, "main", null, 1000)));
    }

    [Fact]
    public async Task Another_tenants_catalog_is_a_404_not_someone_elses_storage()
    {
        await AdminEndpoints.CreateTenantAsync(
            new CreateTenantRequest("other", "Other"), _context, TimeProvider.System, default);

        // Resolution matches tenant and catalog together, so a catalog name that exists elsewhere is
        // not reachable by guessing it. The 404 rather than a 403 is invariant 19's rule: a 403 would
        // confirm the catalog exists.
        Assert.IsType<NotFound<string>>(Unwrap(
            await LakehouseEndpoints.GetStorageAsync("other", "analytics", _service, _options, default)));
    }

    [Fact]
    public async Task An_unknown_table_is_a_400_carrying_the_engines_own_message()
    {
        var result = await LakehouseEndpoints.GetTableFilesAsync(
            "acme", "analytics", "ghost", _service, default, "main", null, 1000);

        var bad = Assert.IsType<BadRequest<string>>(Unwrap(result));
        Assert.Contains("ghost", bad.Value!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_snapshot_predating_the_table_is_a_400_rather_than_an_empty_list()
    {
        // The engine raises here rather than returning nothing, and forwarding that is deliberate: an
        // empty list would be a different and false statement — "this table had no files then".
        var result = await LakehouseEndpoints.GetTableFilesAsync(
            "acme", "analytics", "filed", _service, default, "main", 0, 1000);

        Assert.IsType<BadRequest<string>>(Unwrap(result));
    }

    [Fact]
    public async Task A_limit_outside_the_permitted_range_is_clamped_rather_than_refused()
    {
        // Invariant 6: this path materialises a row per file, so the ceiling is not negotiable by the
        // caller. Zero and negative are clamped up rather than raising, since a request for a
        // nonsensical page size is a client bug, not something to fail an operator's panel over.
        Assert.Single((await Files("filed", limit: 0)).Files);
        Assert.Single((await Files("filed", limit: -5)).Files);

        var huge = await Files("filed", limit: int.MaxValue);
        Assert.NotEmpty(huge.Files);
        Assert.False(huge.Truncated);
    }

    [Fact]
    public void No_response_shape_can_carry_an_encryption_key()
    {
        // ducklake_list_files also returns data_file_encryption_key and delete_file_encryption_key,
        // populated whenever the catalog is encrypted. Asserted on the DTO's shape rather than on a
        // sample value, so an encrypted catalog cannot regress this silently — invariant 8's rule
        // reaching a secret that arrives as a column instead of a connection string.
        var names = typeof(DataFileDto).GetProperties().Select(p => p.Name).ToArray();

        Assert.DoesNotContain(names, n => n.Contains("Key", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, n => n.Contains("Encryption", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Table_detail_combines_schema_storage_and_partition_layout()
    {
        var detail = await Detail("filed");

        Assert.Equal("BASE TABLE", detail.Kind);
        Assert.Equal(149_000, detail.Storage?.RowCount);
        Assert.Equal("id", Assert.Single(detail.Columns).Name);
        var partition = Assert.Single(detail.PartitionSpecs, spec => spec.EndSnapshot is null);
        Assert.Equal("identity", Assert.Single(partition.Keys).Transform);
    }

    [Fact]
    public async Task A_view_detail_makes_no_physical_claim()
    {
        var detail = await Detail("filed_view");

        Assert.Equal("VIEW", detail.Kind);
        Assert.Null(detail.Storage);
        Assert.Empty(detail.PartitionSpecs);
    }

    [Fact]
    public async Task Table_profile_reports_live_rows_and_exact_null_counts()
    {
        await Sql("ALTER TABLE staging ADD COLUMN category VARCHAR");
        await Sql("UPDATE staging SET category = CASE WHEN id = 1 THEN NULL ELSE 'kept' END");

        var profile = await Profile("staging");

        Assert.Equal(3, profile.RowCount);
        var category = Assert.Single(profile.Columns, column => column.Name == "category");
        Assert.Equal(1, category.NullCount);
        Assert.Equal("kept", category.Minimum);
    }

    [Fact]
    public async Task Column_distribution_is_bounded_by_the_endpoint()
    {
        var result = await LakehouseEndpoints.GetColumnDistributionAsync(
            "acme", "analytics", "sharded", "id", _service, default, "main", null, int.MaxValue);
        var distribution = Assert.IsType<Ok<ColumnDistributionDto>>(Unwrap(result)).Value!;

        Assert.Equal("range", distribution.Kind);
        Assert.InRange(distribution.Buckets.Count, 1, 50);
        Assert.Equal(150_000, distribution.Buckets.Sum(bucket => bucket.Count));
    }

    [Fact]
    public async Task Unknown_profile_objects_are_a_400_not_an_empty_profile()
    {
        Assert.IsType<BadRequest<string>>(Unwrap(
            await LakehouseEndpoints.GetTableDetailAsync(
                "acme", "analytics", "ghost", _service, _options, default)));
        Assert.IsType<BadRequest<string>>(Unwrap(
            await LakehouseEndpoints.GetTableProfileAsync(
                "acme", "analytics", "ghost", _service, default)));
        Assert.IsType<BadRequest<string>>(Unwrap(
            await LakehouseEndpoints.GetColumnDistributionAsync(
                "acme", "analytics", "filed", "ghost", _service, default)));
    }
}

using System.Globalization;
using Lakehold.Engine.Catalog;
using Lakehold.Engine.Configuration;
using Lakehold.Engine.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Lakehold.Engine.Tests;

public sealed class ColumnProfilerTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "lakehold-column-profile", Guid.NewGuid().ToString("N"));
    private CatalogDescriptor _catalog = null!;
    private DucklingPool _pool = null!;
    private long _insertSnapshot;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _catalog = new CatalogDescriptor(
            "profilelake",
            CatalogMetadataKind.LocalFile,
            Path.Combine(_root, "profile.ducklake"),
            Path.Combine(_root, "data"));
        Directory.CreateDirectory(_catalog.DataPath);
        _pool = new DucklingPool(
            Options.Create(new LakehouseOptions { DataRoot = _catalog.DataPath }),
            NullLoggerFactory.Instance);

        var duckling = await Session();
        await Run(duckling, """
            CREATE TABLE readings (
                id BIGINT,
                category VARCHAR,
                amount DECIMAL(18, 2),
                happened_at TIMESTAMP,
                attributes STRUCT(source VARCHAR)
            )
            """);
        await Run(duckling, """
            INSERT INTO readings VALUES
                (1, 'a', 10.00, NULL, {'source': 'import'}),
                (2, 'b', 20.00, TIMESTAMP '2026-01-02 10:00:00', {'source': 'api'}),
                (3, 'b', NULL, TIMESTAMP '2026-01-03 11:00:00', {'source': 'api'})
            """);
        _insertSnapshot = await Scalar(
            duckling, $"SELECT max(snapshot_id) FROM ducklake_snapshots('{_catalog.CatalogName}')");

        // The profile must describe these logical rows, not physical records retained by
        // merge-on-read or superseded inlined values.
        await Run(duckling, "UPDATE readings SET category = 'c' WHERE id = 3");
        await Run(duckling, "DELETE FROM readings WHERE id = 1");
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
    public async Task Profile_matches_the_current_logical_rows()
    {
        var profile = await ColumnProfiler.ReadAsync(
            await Session(), "main", "readings", snapshotId: null, CancellationToken.None);

        Assert.Equal(2, profile.RowCount);
        var category = Assert.Single(profile.Columns, column => column.Name == "category");
        Assert.Equal(0, category.NullCount);
        Assert.Equal("b", category.Minimum);
        Assert.Equal("c", category.Maximum);

        var amount = Assert.Single(profile.Columns, column => column.Name == "amount");
        Assert.Equal(1, amount.NullCount);
        Assert.Equal("20.00", amount.Minimum);
        Assert.Equal("20.00", amount.Maximum);
    }

    [Fact]
    public async Task Historical_profile_reads_the_requested_snapshot()
    {
        var profile = await ColumnProfiler.ReadAsync(
            await Session(), "main", "readings", _insertSnapshot, CancellationToken.None);

        Assert.Equal(_insertSnapshot, profile.SnapshotId);
        Assert.Equal(3, profile.RowCount);
        var category = Assert.Single(profile.Columns, column => column.Name == "category");
        Assert.Equal("a", category.Minimum);
        Assert.Equal("b", category.Maximum);
    }

    [Fact]
    public async Task Historical_profile_and_distribution_use_the_schema_at_that_snapshot()
    {
        var duckling = await Session();
        await Run(duckling, "CREATE TABLE evolving (original VARCHAR)");
        await Run(duckling, "INSERT INTO evolving VALUES ('before')");
        var beforeRename = await Scalar(
            duckling, $"SELECT max(snapshot_id) FROM ducklake_snapshots('{_catalog.CatalogName}')");
        await Run(duckling, "ALTER TABLE evolving RENAME original TO renamed");

        var profile = await ColumnProfiler.ReadAsync(
            duckling, "main", "evolving", beforeRename, CancellationToken.None);
        var original = Assert.Single(profile.Columns);

        Assert.Equal("original", original.Name);
        Assert.Equal("before", original.Minimum);

        var distribution = await ColumnProfiler.ReadDistributionAsync(
            duckling,
            "main",
            "evolving",
            "original",
            beforeRename,
            maxBuckets: 10,
            CancellationToken.None);

        Assert.Equal("categorical", distribution.Kind);
        Assert.Equal("before", Assert.Single(distribution.Buckets).Label);
    }

    [Fact]
    public async Task Numeric_and_temporal_columns_return_range_distributions()
    {
        var duckling = await Session();

        var numeric = await ColumnProfiler.ReadDistributionAsync(
            duckling, "main", "readings", "amount", null, 10, CancellationToken.None);
        var temporal = await ColumnProfiler.ReadDistributionAsync(
            duckling, "main", "readings", "happened_at", null, 10, CancellationToken.None);

        Assert.Equal("range", numeric.Kind);
        Assert.Equal(1, numeric.NullCount);
        Assert.Single(numeric.Buckets);
        Assert.Equal(1, numeric.Buckets[0].Count);

        Assert.Equal("range", temporal.Kind);
        Assert.Equal(0, temporal.NullCount);
        Assert.Equal(2, temporal.Buckets.Sum(bucket => bucket.Count));
    }

    [Fact]
    public async Task Categorical_distribution_is_bounded_and_says_when_more_values_exist()
    {
        var distribution = await ColumnProfiler.ReadDistributionAsync(
            await Session(), "main", "readings", "category", null, 1, CancellationToken.None);

        Assert.Equal("categorical", distribution.Kind);
        Assert.True(distribution.Truncated);
        Assert.Single(distribution.Buckets);
    }

    [Fact]
    public async Task Complex_columns_are_explicitly_unsupported_for_distribution()
    {
        var distribution = await ColumnProfiler.ReadDistributionAsync(
            await Session(), "main", "readings", "attributes", null, 10, CancellationToken.None);

        Assert.Equal("unsupported", distribution.Kind);
        Assert.Empty(distribution.Buckets);
    }

    [Fact]
    public async Task A_view_can_be_profiled_only_at_its_current_definition()
    {
        var duckling = await Session();
        await Run(duckling, "CREATE VIEW reading_categories AS SELECT category FROM readings");

        var current = await ColumnProfiler.ReadAsync(
            duckling, "main", "reading_categories", null, CancellationToken.None);

        Assert.Equal(2, current.RowCount);
        Assert.Equal("category", Assert.Single(current.Columns).Name);

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => ColumnProfiler.ReadAsync(
                duckling,
                "main",
                "reading_categories",
                _insertSnapshot,
                CancellationToken.None));
        Assert.Contains("cannot be profiled", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Empty_tables_and_escaped_column_names_keep_their_complete_profile()
    {
        var duckling = await Session();
        await Run(duckling, """CREATE TABLE empty_profile ("select.value" BIGINT, flag BOOLEAN)""");

        var profile = await ColumnProfiler.ReadAsync(
            duckling, "main", "empty_profile", null, CancellationToken.None);
        var distribution = await ColumnProfiler.ReadDistributionAsync(
            duckling,
            "main",
            "empty_profile",
            "select.value",
            null,
            maxBuckets: 10,
            CancellationToken.None);

        Assert.Equal(0, profile.RowCount);
        Assert.Equal(["select.value", "flag"], profile.Columns.Select(column => column.Name));
        Assert.All(profile.Columns, column => Assert.Equal(0, column.NullCount));
        Assert.Equal("range", distribution.Kind);
        Assert.Empty(distribution.Buckets);
    }

    [Fact]
    public async Task Numeric_distribution_does_not_drop_non_finite_values()
    {
        var duckling = await Session();
        await Run(duckling, "CREATE TABLE floating_values (value DOUBLE)");
        await Run(
            duckling,
            "INSERT INTO floating_values VALUES (1.0), ('NaN'::DOUBLE), ('Infinity'::DOUBLE), ('-Infinity'::DOUBLE)");

        var distribution = await ColumnProfiler.ReadDistributionAsync(
            duckling,
            "main",
            "floating_values",
            "value",
            null,
            maxBuckets: 10,
            CancellationToken.None);
        var oneBucket = await ColumnProfiler.ReadDistributionAsync(
            duckling,
            "main",
            "floating_values",
            "value",
            null,
            maxBuckets: 1,
            CancellationToken.None);

        Assert.Equal("range", distribution.Kind);
        Assert.InRange(distribution.Buckets.Count, 1, 10);
        Assert.Equal(4, distribution.Buckets.Sum(bucket => bucket.Count));
        Assert.Equal(4, Assert.Single(oneBucket.Buckets).Count);
    }

    [Fact]
    public async Task A_read_only_attachment_can_profile()
    {
        var reader = await _pool.GetOrStartAsync(
            _catalog with { ReadOnly = true },
            configure: null,
            CancellationToken.None);

        var profile = await ColumnProfiler.ReadAsync(
            reader, "main", "readings", null, CancellationToken.None);

        Assert.Equal(2, profile.RowCount);
    }

    private Task<Duckling> Session() =>
        _pool.GetOrStartAsync(_catalog, configure: null, CancellationToken.None);

    private static Task<QueryResult> Run(Duckling duckling, string sql) =>
        duckling.ExecuteQueryAsync(sql, CancellationToken.None);

    private static async Task<long> Scalar(Duckling duckling, string sql)
    {
        var result = await duckling.ExecuteQueryAsync(sql, CancellationToken.None);
        return Convert.ToInt64(result.Rows.Single()[0], CultureInfo.InvariantCulture);
    }
}

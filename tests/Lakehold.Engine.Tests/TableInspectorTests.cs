using Lakehold.Engine.Catalog;
using Lakehold.Engine.Configuration;
using Lakehold.Engine.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Lakehold.Engine.Tests;

public sealed class TableInspectorTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "lakehold-table-detail", Guid.NewGuid().ToString("N"));
    private CatalogDescriptor _catalog = null!;
    private DucklingPool _pool = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _catalog = new CatalogDescriptor(
            "detaillake",
            CatalogMetadataKind.LocalFile,
            Path.Combine(_root, "detail.ducklake"),
            Path.Combine(_root, "data"));
        Directory.CreateDirectory(_catalog.DataPath);
        _pool = new DucklingPool(
            Options.Create(new LakehouseOptions { DataRoot = _catalog.DataPath }),
            NullLoggerFactory.Instance);

        var duckling = await Session();
        await Run(duckling, """
            CREATE TABLE events (
                region VARCHAR NOT NULL,
                happened_at TIMESTAMP,
                amount DECIMAL(18, 2)
            )
            """);
        await Run(duckling, "ALTER TABLE events SET PARTITIONED BY (region, month(happened_at))");
        await Run(duckling, """
            INSERT INTO events VALUES
                ('north', TIMESTAMP '2026-01-03 10:00:00', 12.50),
                ('south', TIMESTAMP '2026-02-04 11:00:00', 18.75)
            """);
        await Run(duckling, "CREATE VIEW event_names AS SELECT region FROM events");
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
    public async Task Detail_combines_logical_storage_and_partition_information()
    {
        var detail = await TableInspector.ReadAsync(
            await Session(), "main", "events", CancellationToken.None);

        Assert.Equal("BASE TABLE", detail.Kind);
        Assert.Equal(2, detail.Storage?.RowCount);
        Assert.Equal(["region", "happened_at", "amount"], detail.Columns.Select(c => c.Name));
        Assert.False(detail.Columns[0].IsNullable);

        var current = Assert.Single(detail.PartitionSpecs, spec => spec.EndSnapshot is null);
        Assert.Equal(
            [("region", "identity"), ("happened_at", "month")],
            current.Keys.Select(key => (key.ColumnName, key.Transform)));
    }

    [Fact]
    public async Task A_view_has_logical_columns_and_no_physical_claims()
    {
        var detail = await TableInspector.ReadAsync(
            await Session(), "main", "event_names", CancellationToken.None);

        Assert.Equal("VIEW", detail.Kind);
        Assert.Single(detail.Columns);
        Assert.Null(detail.Storage);
        Assert.Empty(detail.PartitionSpecs);
    }

    [Fact]
    public async Task Detail_preserves_superseded_partition_specifications()
    {
        var duckling = await Session();
        await Run(duckling, "ALTER TABLE events SET PARTITIONED BY (month(happened_at))");

        var detail = await TableInspector.ReadAsync(
            duckling, "main", "events", CancellationToken.None);

        Assert.Equal(2, detail.PartitionSpecs.Count);
        var current = Assert.Single(detail.PartitionSpecs, spec => spec.EndSnapshot is null);
        var previous = Assert.Single(detail.PartitionSpecs, spec => spec.EndSnapshot is not null);
        Assert.Equal("month", Assert.Single(current.Keys).Transform);
        Assert.Equal(["identity", "month"], previous.Keys.Select(key => key.Transform));
        Assert.Equal(current.BeginSnapshot, previous.EndSnapshot);
    }

    [Fact]
    public async Task Catalog_derived_names_are_escaped_not_rejected()
    {
        var duckling = await Session();
        await Run(duckling, """CREATE TABLE "order.items" ("select" BIGINT)""");
        await Run(duckling, """INSERT INTO "order.items" VALUES (42)""");

        var detail = await TableInspector.ReadAsync(
            duckling, "main", "order.items", CancellationToken.None);

        Assert.Equal(1, detail.Storage?.RowCount);
        Assert.Equal("select", Assert.Single(detail.Columns).Name);
    }

    [Fact]
    public async Task A_read_only_attachment_can_inspect_a_table()
    {
        var reader = await _pool.GetOrStartAsync(
            _catalog with { ReadOnly = true },
            configure: null,
            CancellationToken.None);

        var detail = await TableInspector.ReadAsync(
            reader, "main", "events", CancellationToken.None);

        Assert.Equal(2, detail.Storage?.RowCount);
    }

    [Fact]
    public async Task Introspection_reads_the_primary_catalog_when_a_share_has_the_same_table_name()
    {
        var shareRoot = Path.Combine(_root, "share");
        Directory.CreateDirectory(shareRoot);
        var shareCatalog = new CatalogDescriptor(
            "shared",
            CatalogMetadataKind.LocalFile,
            Path.Combine(shareRoot, "shared.ducklake"),
            Path.Combine(shareRoot, "data"));
        Directory.CreateDirectory(shareCatalog.DataPath);

        var share = await _pool.GetOrStartAsync(
            shareCatalog, configure: null, CancellationToken.None);
        await Run(share, "CREATE TABLE events (shared_only BOOLEAN)");
        await _pool.EvictAsync(shareCatalog.TenantKey, shareCatalog.CatalogId);

        // Reattach the primary with the share alongside it. Both catalogs deliberately have
        // main.events; detail must not merge information_schema rows from the read-only share.
        await _pool.EvictAsync(_catalog.TenantKey, _catalog.CatalogId);
        var primary = await _pool.GetOrStartAsync(
            _catalog with
            {
                AdditionalCatalogs =
                [
                    new AttachedCatalog(
                        shareCatalog.CatalogName,
                        shareCatalog.MetadataSource,
                        CatalogMetadataKind.LocalFile),
                ],
            },
            configure: null,
            CancellationToken.None);

        var detail = await TableInspector.ReadAsync(
            primary, "main", "events", CancellationToken.None);
        var schemas = await CatalogBrowser.ReadSchemasAsync(primary, CancellationToken.None);
        var catalogTable = Assert.Single(
            Assert.Single(schemas, schema => schema.Name == "main").Tables,
            table => table.Name == "events");

        Assert.Equal(["region", "happened_at", "amount"], detail.Columns.Select(c => c.Name));
        Assert.Equal(["region", "happened_at", "amount"], catalogTable.Columns.Select(c => c.Name));
    }

    [Fact]
    public async Task An_unknown_table_is_not_reported_as_an_empty_detail()
    {
        var duckling = await Session();
        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => TableInspector.ReadAsync(
                duckling, "main", "missing", CancellationToken.None));

        Assert.Contains("does not exist", exception.Message, StringComparison.Ordinal);
    }

    private Task<Duckling> Session() =>
        _pool.GetOrStartAsync(_catalog, configure: null, CancellationToken.None);

    private static Task<QueryResult> Run(Duckling duckling, string sql) =>
        duckling.ExecuteQueryAsync(sql, CancellationToken.None);
}

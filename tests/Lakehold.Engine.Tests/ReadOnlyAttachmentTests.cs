using Lakehold.Engine.Catalog;
using Lakehold.Engine.Configuration;
using Lakehold.Engine.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Lakehold.Engine.Tests;

/// <summary>
///     Cover for read-only capability by attachment (phase 2): a read-only descriptor produces a
///     session whose catalog is attached read-only, so a write fails in the engine rather than by a
///     string check; a read-only and a read-write session for the same catalog are distinct; and both
///     are evicted when the catalog is forgotten.
/// </summary>
public sealed class ReadOnlyAttachmentTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lakehold-ro", Guid.NewGuid().ToString("N"));
    private CatalogDescriptor _writable = null!;
    private CatalogDescriptor _readOnly = null!;
    private DucklingPool _pool = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);

        var options = new LakehouseOptions { DataRoot = Path.Combine(_root, "data") };
        _writable = new CatalogDescriptor(
            "rolake",
            CatalogMetadataKind.LocalFile,
            Path.Combine(_root, "test.ducklake"),
            Path.Combine(_root, "data"));
        _readOnly = _writable with { ReadOnly = true };

        Directory.CreateDirectory(_writable.DataPath);
        _pool = new DucklingPool(Options.Create(options), NullLoggerFactory.Instance);

        // Seed a row through the writable session so the read-only session has something to read.
        var writer = await _pool.GetOrStartAsync(_writable, configure: null, CancellationToken.None);
        await writer.ExecuteQueryAsync("CREATE TABLE t (id BIGINT)", CancellationToken.None);
        await writer.ExecuteQueryAsync("INSERT INTO t VALUES (1)", CancellationToken.None);
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
    public async Task A_read_only_session_refuses_writes_in_the_engine()
    {
        var reader = await _pool.GetOrStartAsync(_readOnly, configure: null, CancellationToken.None);

        await Assert.ThrowsAsync<DuckDB.NET.Data.DuckDBException>(
            () => reader.ExecuteQueryAsync("INSERT INTO t VALUES (2)", CancellationToken.None));

        // The same session still serves reads normally.
        var read = await reader.ExecuteQueryAsync("SELECT count(*) FROM t", CancellationToken.None);
        Assert.Single(read.Rows);
    }

    [Fact]
    public async Task Read_only_and_read_write_get_different_sessions_for_one_catalog()
    {
        var writer = await _pool.GetOrStartAsync(_writable, configure: null, CancellationToken.None);
        var reader = await _pool.GetOrStartAsync(_readOnly, configure: null, CancellationToken.None);

        Assert.NotSame(writer, reader);
        Assert.False(writer.Catalog.ReadOnly);
        Assert.True(reader.Catalog.ReadOnly);

        // Both modes are one catalog by name, and evicting by name drops both.
        Assert.Equal(["rolake"], _pool.WarmCatalogs);
        await _pool.EvictAsync("rolake");
        Assert.Empty(_pool.WarmCatalogs);
    }
}

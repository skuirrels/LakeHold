using DuckDB.EFCoreProvider.Extensions;
using Lakehold.Api.Endpoints;
using Lakehold.ControlPlane.Data;
using Lakehold.Engine.Catalog;
using Lakehold.Engine.Configuration;
using Lakehold.Engine.Execution;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Lakehold.Api.Tests;

/// <summary>
///     Cover for where a restore actually puts the catalog it rebuilds.
/// </summary>
/// <remarks>
///     <para>
///         Restore is used under pressure, and the operator's next move is to look at the file it
///         wrote. Left to the framework a bare name resolves against the server's working directory —
///         beside the binary in development, and somewhere nobody would look under Docker — and the
///         response echoed the caller's own unresolved string back at them, so nothing on screen said
///         otherwise. That is a correct restore an operator cannot find.
///     </para>
///     <para>
///         Nothing here relaxes the refusal to overwrite. That property is what makes restore safe;
///         these tests pin down where the new file lands and what the caller is told about it.
///     </para>
/// </remarks>
public sealed class RestoreTargetTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lakehold-restore-target", Guid.NewGuid().ToString("N"));
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
            BackupRoot = Path.Combine(_root, "backups"),
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

        await _service.ExecuteAsync("acme", "analytics", "CREATE TABLE people (id BIGINT)", default);
        await _service.ExecuteAsync("acme", "analytics", "INSERT INTO people VALUES (1), (2), (3)", default);
        await _service.RunMaintenanceAsync("acme", "analytics", "backup", apply: true, cancellationToken: default);
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

    [Fact]
    public async Task A_bare_name_lands_beside_the_servers_other_catalogs()
    {
        var result = await _service.RestoreBackupAsync(
            "acme", "analytics", generation: null, "rebuilt.ducklake", default);

        var expected = Path.Combine(_options.Value.MetadataRoot, "rebuilt.ducklake");
        Assert.Equal(expected, result.MetadataPath);
        Assert.True(File.Exists(expected), "the rebuilt catalog must exist where the response says it does");

        // The one place it must not be: resolved against wherever the server process was started.
        Assert.False(File.Exists(Path.Combine(Directory.GetCurrentDirectory(), "rebuilt.ducklake")));
    }

    [Fact]
    public async Task An_absolute_target_is_honoured_exactly_as_given()
    {
        var target = Path.Combine(_root, "elsewhere", "rebuilt.ducklake");

        var result = await _service.RestoreBackupAsync(
            "acme", "analytics", generation: null, target, default);

        // An operator who names a path means that path; the metadata root is a default, not a jail.
        Assert.Equal(target, result.MetadataPath);
        Assert.True(File.Exists(target));
    }

    [Fact]
    public async Task The_reported_path_is_absolute_and_the_rows_are_all_there()
    {
        var result = await _service.RestoreBackupAsync(
            "acme", "analytics", generation: null, "rebuilt.ducklake", default);

        // The response is the only thing the operator sees. A relative string here is an answer they
        // cannot act on, whatever it resolves to.
        Assert.True(Path.IsPathRooted(result.MetadataPath));
        Assert.True(result.TablesRestored > 0);

        var restored = new CatalogDescriptor(
            "rebuilt",
            CatalogMetadataKind.LocalFile,
            result.MetadataPath,
            CatalogStorageNamespace.Under(_options.Value.DataRoot, "acme", "analytics"));

        await using var pool = new DucklingPool(_options, NullLoggerFactory.Instance);
        var session = await pool.GetOrStartAsync(restored, configure: null, CancellationToken.None);
        var rows = await session.ExecuteQueryAsync("SELECT count(*) FROM people", CancellationToken.None);

        Assert.Equal(3L, Convert.ToInt64(rows.Rows[0][0]));
    }

    [Fact]
    public async Task Restoring_the_same_bare_name_twice_is_still_refused()
    {
        await _service.RestoreBackupAsync("acme", "analytics", generation: null, "rebuilt.ducklake", default);

        // Anchoring the relative path must not have introduced a way to write past an existing
        // catalog: the second attempt has to resolve to the same file and be refused there.
        var second = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.RestoreBackupAsync("acme", "analytics", generation: null, "rebuilt.ducklake", default));

        Assert.Contains("already exists", second.Message, StringComparison.OrdinalIgnoreCase);

        // And the message names the file it refused, resolved — not the string the caller typed.
        Assert.Contains(_options.Value.MetadataRoot, second.Message, StringComparison.Ordinal);
    }
}

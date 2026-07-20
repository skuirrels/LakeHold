using Lakehold.Engine.Catalog;
using Lakehold.Engine.Configuration;
using Lakehold.Engine.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Globalization;
using Xunit;

namespace Lakehold.Engine.Tests;

/// <summary>
///     End-to-end cover for the backup/restore contract.
/// </summary>
/// <remarks>
///     These assert behaviour that compilation cannot: that a restored catalog still honours
///     deletions, still carries its history, and that an incomplete backup is refused. The recovery
///     path is only exercised during an incident, so it has to be exercised here instead.
/// </remarks>
public sealed class CatalogBackupRoundTripTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lakehold-tests", Guid.NewGuid().ToString("N"));
    private LakehouseOptions _options = null!;
    private CatalogDescriptor _catalog = null!;
    private DucklingPool _pool = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);

        _options = new LakehouseOptions
        {
            DataRoot = Path.Combine(_root, "data"),
            BackupRoot = Path.Combine(_root, "backups"),
            BackupRetainCount = 2,
        };

        _catalog = new CatalogDescriptor(
            "testlake",
            CatalogMetadataKind.LocalFile,
            Path.Combine(_root, "test.ducklake"),
            Path.Combine(_root, "data"));

        Directory.CreateDirectory(_catalog.DataPath);
        _pool = new DucklingPool(Options.Create(_options), NullLoggerFactory.Instance);

        var duckling = await GetSessionAsync();
        await Run(duckling, "CREATE TABLE people (id BIGINT, name VARCHAR, email VARCHAR)");
        await Run(duckling, "INSERT INTO people SELECT i, 'name' || i, 'e' || i || '@x.com' FROM range(5000) t(i)");
        await Run(duckling, "DELETE FROM people WHERE id < 10");
        await Run(duckling, "UPDATE people SET email = 'redacted' WHERE id BETWEEN 20 AND 29");
        await Run(duckling, "CREATE VIEW vip AS SELECT * FROM people WHERE id > 4990");
        await Run(duckling, "CALL ducklake_flush_inlined_data('testlake')");
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
    public async Task Backup_writes_outside_the_data_path()
    {
        var result = await BackupAsync();

        // Regression cover for the defect this feature shipped with: backups nested under the data
        // path were swept by DuckLake's own orphan cleanup once they aged past its cutoff.
        Assert.False(
            result.Location.StartsWith(_catalog.DataPath, StringComparison.Ordinal),
            $"backup at '{result.Location}' must not live under the data path '{_catalog.DataPath}'");
        Assert.True(result.TableCount > 0);
    }

    [Fact]
    public async Task Backup_writes_a_manifest_listing_every_exported_table()
    {
        var result = await BackupAsync();

        var manifestPath = Path.Combine(result.Location, CatalogBackup.ManifestFileName);
        Assert.True(File.Exists(manifestPath), "the completion manifest must exist");

        var generations = await CatalogRestore.ListGenerationsAsync(
            _options, _catalog.CatalogName, configure: null, CancellationToken.None);
        var generation = Assert.Single(generations);
        Assert.True(generation.IsComplete);
        Assert.Equal(result.TableCount, generation.Manifest!.Tables.Count);

        foreach (var table in generation.Manifest.Tables)
        {
            Assert.True(
                File.Exists(Path.Combine(result.Location, $"{table.Name}.parquet")),
                $"manifest names '{table.Name}' but no Parquet was written for it");
        }
    }

    [Fact]
    public async Task Restore_reproduces_contents_deletions_and_history()
    {
        var truth = await ReadTruthAsync();
        await BackupAsync();

        var target = Path.Combine(_root, "restored.ducklake");
        var restore = await CatalogRestore.RestoreAsync(
            _options, _catalog.CatalogName, generation: null, target, _catalog.DataPath, CancellationToken.None);

        Assert.True(restore.TablesRestored > 0);
        Assert.True(File.Exists(target));

        var restored = new CatalogDescriptor(
            "restoredlake", CatalogMetadataKind.LocalFile, target, _catalog.DataPath);

        await using var pool = new DucklingPool(Options.Create(_options), NullLoggerFactory.Instance);
        var session = await pool.GetOrStartAsync(restored, configure: null, CancellationToken.None);

        Assert.Equal(truth.Rows, await ScalarAsync(session, "SELECT count(*) FROM people"));

        // The failure this whole feature exists to prevent: a rebuild that silently reinstates rows
        // someone was legally obliged to erase.
        Assert.Equal("0", await ScalarAsync(session, "SELECT count(*) FROM people WHERE id < 10"));
        Assert.Equal("redacted", await ScalarAsync(session, "SELECT email FROM people WHERE id = 25"));
        Assert.Equal("0", await ScalarAsync(session, "SELECT count(*) FROM (SELECT id FROM people GROUP BY id HAVING count(*) > 1)"));

        // History is the thing a row-level rebuild from Parquet alone cannot recover.
        Assert.Equal(truth.Snapshots, await ScalarAsync(session, "SELECT count(*) FROM ducklake_snapshots('restoredlake')"));
        Assert.Equal(truth.PreDeleteRows, await ScalarAsync(session, "SELECT count(*) FROM people AT (VERSION => 2)"));
    }

    [Fact]
    public async Task Restore_refuses_a_generation_with_no_manifest()
    {
        var result = await BackupAsync();
        File.Delete(Path.Combine(result.Location, CatalogBackup.ManifestFileName));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => CatalogRestore.RestoreAsync(
            _options, _catalog.CatalogName, result.Location.Split(Path.DirectorySeparatorChar)[^1],
            Path.Combine(_root, "nope.ducklake"), _catalog.DataPath, CancellationToken.None));

        Assert.Contains("incomplete", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Restore_refuses_to_overwrite_an_existing_catalog()
    {
        await BackupAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => CatalogRestore.RestoreAsync(
            _options, _catalog.CatalogName, generation: null,
            _catalog.MetadataSource, _catalog.DataPath, CancellationToken.None));

        Assert.Contains("already exists", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Retention_keeps_only_the_configured_number_of_generations()
    {
        // BackupRetainCount is 2; a third backup must evict the oldest.
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z", CultureInfo.InvariantCulture));
        var duckling = await GetSessionAsync();

        for (var i = 0; i < 3; i++)
        {
            await duckling.InvokeAsync(
                ct => CatalogBackup.WriteAsync(duckling, _options, clock, ct),
                CancellationToken.None);
            clock.Advance(TimeSpan.FromMinutes(1));
        }

        var generations = await CatalogRestore.ListGenerationsAsync(
            _options, _catalog.CatalogName, configure: null, CancellationToken.None);
        Assert.Equal(2, generations.Count);
    }

    private async Task<CatalogBackupResult> BackupAsync()
    {
        var duckling = await GetSessionAsync();
        return await duckling.InvokeAsync(
            ct => CatalogBackup.WriteAsync(duckling, _options, TimeProvider.System, ct),
            CancellationToken.None);
    }

    private async Task<(string Rows, string Snapshots, string PreDeleteRows)> ReadTruthAsync()
    {
        var d = await GetSessionAsync();
        return (
            await ScalarAsync(d, "SELECT count(*) FROM people"),
            await ScalarAsync(d, "SELECT count(*) FROM ducklake_snapshots('testlake')"),
            await ScalarAsync(d, "SELECT count(*) FROM people AT (VERSION => 2)"));
    }

    private Task<Duckling> GetSessionAsync()
        => _pool.GetOrStartAsync(_catalog, configure: null, CancellationToken.None);

    private static async Task Run(Duckling duckling, string sql)
        => await duckling.ExecuteQueryAsync(sql, CancellationToken.None);

    private static async Task<string> ScalarAsync(Duckling duckling, string sql)
    {
        var result = await duckling.ExecuteQueryAsync(sql, CancellationToken.None);
        return Convert.ToString(result.Rows[0][0], CultureInfo.InvariantCulture) ?? string.Empty;
    }

    /// <summary>Minimal controllable clock so retention can be tested without sleeping.</summary>
    private sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }
}

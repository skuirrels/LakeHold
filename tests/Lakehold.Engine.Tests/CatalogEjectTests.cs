using System.Globalization;
using DuckDB.NET.Data;
using Lakehold.Engine.Catalog;
using Lakehold.Engine.Configuration;
using Lakehold.Engine.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Lakehold.Engine.Tests;

/// <summary>
///     Cover for the verified eject bundle: that its Parquet reads back with no DuckLake in the loop,
///     that verification and the signature actually detect tampering, and that history is optional.
/// </summary>
/// <remarks>
///     The whole value of an eject is that a third party can trust it without running Lakehold, so
///     these assertions deliberately read the bundle with a plain <see cref="DuckDBConnection"/> that
///     has never loaded the <c>ducklake</c> extension — the same position a downstream consumer is in.
/// </remarks>
public sealed class CatalogEjectTests : IAsyncLifetime
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
            EjectRoot = Path.Combine(_root, "ejects"),
        };

        _catalog = new CatalogDescriptor(
            "ejectlake",
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
        await Run(duckling, "CREATE TABLE tiny (k INTEGER)");
        await Run(duckling, "INSERT INTO tiny VALUES (1), (2)");
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
    public async Task Eject_writes_reader_agnostic_parquet_with_deletions_and_updates_applied()
    {
        var result = await EjectAsync(includeHistory: false);

        Assert.True(result.Verified);
        Assert.Equal(2, result.TableCount);

        // The bundle must live outside the data path, like a backup, or DuckLake's orphan cleanup
        // would eventually delete it.
        Assert.False(result.Location.StartsWith(_catalog.DataPath, StringComparison.Ordinal));

        var peopleParquet = Path.Combine(result.Location, "data", "main", "people.parquet");
        Assert.True(File.Exists(peopleParquet), "the clean data export must exist");

        // Read the bundle with a plain DuckDB connection — no ducklake extension, exactly a third
        // party's position. Deletions applied (4990, not 5000), updates applied, no resurrected rows.
        Assert.Equal(4990L, PlainCount(peopleParquet));
        Assert.Equal(0L, PlainCount(peopleParquet, "id < 10"));
        Assert.Equal("redacted", PlainScalar(peopleParquet, "SELECT email FROM read_parquet('{0}') WHERE id = 25"));

        // Deletes must not have been re-materialised as an internal sidecar column.
        var columns = PlainColumns(peopleParquet);
        Assert.Equal(["id", "name", "email"], columns);
    }

    [Fact]
    public async Task Eject_manifest_row_counts_match_an_independent_reader()
    {
        var result = await EjectAsync(includeHistory: false);

        var manifest = ReadManifest(result.Location);
        Assert.NotNull(manifest);

        foreach (var table in manifest!.DataTables)
        {
            var path = Path.Combine(result.Location, "data", table.Schema, $"{table.Table}.parquet");
            Assert.Equal(table.RowCount, table.VerifiedRowCount);
            Assert.Equal(table.RowCount, PlainCount(path));
            Assert.NotNull(table.Sha256);
        }
    }

    [Fact]
    public async Task Eject_signs_the_manifest_and_the_signature_detects_tampering()
    {
        _options.EjectSigningKey = "correct horse battery staple";

        var result = await EjectAsync(includeHistory: false);
        Assert.True(result.IsSigned);

        var manifest = ReadManifest(result.Location)!;
        Assert.Equal(EjectSignature.Algorithm, manifest.SignatureAlgorithm);
        Assert.True(EjectSignature.Verify(manifest, _options.EjectSigningKey), "an untouched manifest must verify");

        // A different key must not verify.
        Assert.False(EjectSignature.Verify(manifest, "wrong key"));

        // Altering an attested row count must invalidate the signature — the whole point of signing
        // the attestation rather than just storing it.
        var tampered = manifest with
        {
            DataTables = [manifest.DataTables[0] with { RowCount = manifest.DataTables[0].RowCount + 1 }, .. manifest.DataTables.Skip(1)],
        };
        Assert.False(EjectSignature.Verify(tampered, _options.EjectSigningKey));
    }

    [Fact]
    public async Task Eject_with_history_copies_the_metadata_catalog()
    {
        var result = await EjectAsync(includeHistory: true);
        Assert.True(result.IncludesHistory);

        var manifest = ReadManifest(result.Location)!;
        Assert.NotEmpty(manifest.MetadataTables);

        // The metadata copy is what lets snapshots and time travel survive the export, so at least the
        // snapshot table must be present.
        Assert.Contains(manifest.MetadataTables, t => t.Name.Contains("snapshot", StringComparison.OrdinalIgnoreCase));

        var catalogDir = Path.Combine(result.Location, "catalog");
        Assert.True(Directory.Exists(catalogDir));
        Assert.NotEmpty(Directory.GetFiles(catalogDir, "*.parquet"));
    }

    [Fact]
    public async Task Eject_bundles_are_listed_newest_first()
    {
        var clock = new ManualClock(DateTimeOffset.Parse("2026-02-01T00:00:00Z", CultureInfo.InvariantCulture));
        var duckling = await GetSessionAsync();

        for (var i = 0; i < 2; i++)
        {
            await duckling.InvokeAsync(
                ct => CatalogEject.WriteAsync(duckling, _options, includeHistory: false, clock, ct),
                CancellationToken.None);
            clock.Advance(TimeSpan.FromMinutes(1));
        }

        var bundles = CatalogEject.ListBundles(_options, _catalog.CatalogName);
        Assert.Equal(2, bundles.Count);
        Assert.All(bundles, b => Assert.True(b.IsComplete));
        Assert.True(string.CompareOrdinal(bundles[0].Bundle, bundles[1].Bundle) > 0, "newest bundle must be first");
    }

    private async Task<CatalogEjectResult> EjectAsync(bool includeHistory)
    {
        var duckling = await GetSessionAsync();
        return await duckling.InvokeAsync(
            ct => CatalogEject.WriteAsync(duckling, _options, includeHistory, TimeProvider.System, ct),
            CancellationToken.None);
    }

    private static EjectManifest? ReadManifest(string location)
    {
        var path = Path.Combine(location, CatalogEject.ManifestFileName);
        return File.Exists(path)
            ? System.Text.Json.JsonSerializer.Deserialize<EjectManifest>(File.ReadAllText(path))
            : null;
    }

    private static long PlainCount(string parquetPath, string? where = null)
    {
        var predicate = where is null ? string.Empty : $" WHERE {where}";
        return Convert.ToInt64(
            PlainScalarRaw($"SELECT count(*) FROM read_parquet('{parquetPath}'){predicate}"),
            CultureInfo.InvariantCulture);
    }

    private static string? PlainScalar(string parquetPath, string sqlTemplate)
        => Convert.ToString(
            PlainScalarRaw(string.Format(CultureInfo.InvariantCulture, sqlTemplate, parquetPath)),
            CultureInfo.InvariantCulture);

    private static List<string> PlainColumns(string parquetPath)
    {
        using var connection = new DuckDBConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"DESCRIBE SELECT * FROM read_parquet('{parquetPath}')";
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    /// <summary>Runs SQL on a plain DuckDB connection with the ducklake extension never loaded.</summary>
    private static object? PlainScalarRaw(string sql)
    {
        using var connection = new DuckDBConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private Task<Duckling> GetSessionAsync() => _pool.GetOrStartAsync(_catalog, configure: null, CancellationToken.None);

    private static async Task Run(Duckling duckling, string sql)
        => await duckling.ExecuteQueryAsync(sql, CancellationToken.None);

    /// <summary>Minimal controllable clock so bundle timestamps differ without sleeping.</summary>
    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }
}

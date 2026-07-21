using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lakehold.Engine.Configuration;
using Lakehold.Engine.Execution;

namespace Lakehold.Engine.Catalog;

/// <summary>One user table exported into an eject bundle, with its attestation.</summary>
/// <param name="Schema">Schema the table belongs to.</param>
/// <param name="Table">Table name.</param>
/// <param name="RelativePath">Path of the Parquet file within the bundle, using '/' separators.</param>
/// <param name="RowCount">
///     Rows the live catalog reports, with deletions and updates already applied.
/// </param>
/// <param name="VerifiedRowCount">
///     Rows counted back out of the written Parquet with a plain reader. Equal to
///     <paramref name="RowCount"/> for a trustworthy bundle; the eject refuses to write a manifest if
///     any table disagrees.
/// </param>
/// <param name="Sha256">
///     Lowercase hex SHA-256 of the written file, or null when the bundle is on an object store where
///     hashing the bytes would mean downloading them.
/// </param>
/// <param name="Bytes">Size of the written file, or null for a remote bundle.</param>
public sealed record EjectedTable(
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("table")] string Table,
    [property: JsonPropertyName("relativePath")] string RelativePath,
    [property: JsonPropertyName("rowCount")] long RowCount,
    [property: JsonPropertyName("verifiedRowCount")] long VerifiedRowCount,
    [property: JsonPropertyName("sha256")] string? Sha256,
    [property: JsonPropertyName("bytes")] long? Bytes);

/// <summary>
///     Contents of <c>MANIFEST.json</c>, written last so its presence proves the eject completed and
///     every table verified.
/// </summary>
public sealed record EjectManifest
{
    /// <summary>Manifest schema version, so a future reader can reject shapes it cannot interpret.</summary>
    [JsonPropertyName("formatVersion")]
    public int FormatVersion { get; init; } = 1;

    [JsonPropertyName("catalogName")]
    public required string CatalogName { get; init; }

    [JsonPropertyName("createdUtc")]
    public required DateTimeOffset CreatedUtc { get; init; }

    /// <summary>The snapshot the exported data reflects — the bundle's point in time.</summary>
    [JsonPropertyName("snapshotId")]
    public long? SnapshotId { get; init; }

    [JsonPropertyName("duckDbVersion")]
    public string? DuckDbVersion { get; init; }

    /// <summary>Whether the bundle also carries the metadata catalog, so history is recoverable.</summary>
    [JsonPropertyName("includesHistory")]
    public bool IncludesHistory { get; init; }

    /// <summary>The reader-agnostic data files and their attestation.</summary>
    [JsonPropertyName("dataTables")]
    public required IReadOnlyList<EjectedTable> DataTables { get; init; }

    /// <summary>The copied metadata catalog tables, present only when <see cref="IncludesHistory"/>.</summary>
    [JsonPropertyName("metadataTables")]
    public IReadOnlyList<BackupTable> MetadataTables { get; init; } = [];

    /// <summary>Signature algorithm, or null when the deployment configured no signing key.</summary>
    [JsonPropertyName("signatureAlgorithm")]
    public string? SignatureAlgorithm { get; init; }

    /// <summary>
    ///     Base64 HMAC over the manifest's load-bearing fields, or null when unsigned. Recomputed by a
    ///     holder of the same key to prove the attestation is authentic and unaltered.
    /// </summary>
    [JsonPropertyName("signature")]
    public string? Signature { get; init; }
}

/// <summary>Outcome of an eject.</summary>
/// <param name="Location">Directory or prefix the bundle was written to.</param>
/// <param name="TableCount">Number of user tables exported.</param>
/// <param name="TotalRows">Total rows across all exported tables.</param>
/// <param name="Verified">
///     True when every table's re-read row count matched the catalog's. Always true on success,
///     because a mismatch aborts the eject before the manifest is written.
/// </param>
/// <param name="DigestDeferred">
///     True when file digests were skipped because the bundle is on an object store. Reported rather
///     than silently omitted, so an operator who expects tamper-evident hashes knows they must run an
///     external integrity check instead.
/// </param>
/// <param name="IsSigned">True when the manifest carries a signature.</param>
/// <param name="IncludesHistory">True when the metadata catalog was copied alongside the data.</param>
public sealed record CatalogEjectResult(
    string Location,
    int TableCount,
    long TotalRows,
    bool Verified,
    bool DigestDeferred,
    bool IsSigned,
    bool IncludesHistory);

/// <summary>An eject bundle available on disk.</summary>
/// <param name="Bundle">Timestamped directory name.</param>
/// <param name="Location">Full path or prefix.</param>
/// <param name="Manifest">Parsed manifest, or null when the bundle is incomplete.</param>
public sealed record EjectBundle(string Bundle, string Location, EjectManifest? Manifest)
{
    /// <summary>Whether the bundle completed and every table verified.</summary>
    public bool IsComplete => Manifest is not null;
}

/// <summary>
///     Produces a verified, reader-agnostic export of a catalog's data — the exit path as a product
///     feature rather than a document.
/// </summary>
/// <remarks>
///     <para>
///         DuckLake is Parquet files plus a SQL catalog. The catalog is load-bearing: deletions are
///         merge-on-read sidecars, updates leave superseded rows in place, and small commits may be
///         inlined into the metadata database and not written to Parquet at all. A naive
///         <c>read_parquet</c> over the data path therefore resurrects deleted rows, duplicates
///         updated ones, and can miss the newest data entirely. See <c>docs/EXIT-PATH.md</c>.
///     </para>
///     <para>
///         An eject sidesteps every one of those hazards by re-materialising each table through the
///         catalog — <c>COPY (SELECT * FROM table) TO …</c> — so deletions and updates are applied,
///         inlined data is included, and the internal row-id and snapshot-id columns are dropped. The
///         result is ordinary Parquet with no DuckLake-specific framing, verified on DuckDB 1.5.4 to
///         read back correctly with the <c>ducklake</c> extension not even loaded.
///     </para>
///     <para>
///         Because it only reads, an eject never mutates the catalog and works on a read-only share.
///         It does not need to flush inlined data first, unlike a raw copy of the data path, because
///         <c>SELECT *</c> already merges inlined rows with file rows.
///     </para>
///     <para>
///         Every export is independently verified: each written file is counted back with a plain
///         reader and compared to the catalog's own count, and any disagreement aborts the eject
///         before a manifest is written. The manifest records per-table row counts and per-file
///         SHA-256 digests, and — when a signing key is configured — an HMAC over those facts, so the
///         bundle is a tamper-evident attestation rather than a folder of files a reader must trust.
///     </para>
///     <para>
///         Like <see cref="CatalogBackup"/>, this runs with the session gate already held and uses the
///         unguarded execute path throughout: re-entering the gate would deadlock, and holding it for
///         the whole export stops a concurrent write changing the data midway.
///     </para>
/// </remarks>
public static class CatalogEject
{
    /// <summary>Name of the completion manifest. Its absence marks a bundle as unusable.</summary>
    public const string ManifestFileName = "MANIFEST.json";

    private const string DataDirectory = "data";
    private const string CatalogDirectory = "catalog";

    private static readonly JsonSerializerOptions ManifestJson = new() { WriteIndented = true };

    /// <summary>
    ///     Takes the session gate and writes a verified bundle — the entry point for callers outside
    ///     the engine, mirroring how <see cref="LakehouseMaintenance"/> wraps its operations.
    /// </summary>
    /// <remarks>
    ///     Holding the gate for the whole export is what makes the attestation honest: no write can
    ///     land between the copy and the verification, so the counts describe one consistent state.
    /// </remarks>
    public static Task<CatalogEjectResult> RunAsync(
        Duckling duckling,
        LakehouseOptions options,
        bool includeHistory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(duckling);

        return duckling.InvokeAsync(
            ct => WriteAsync(duckling, options, includeHistory, TimeProvider.System, ct),
            cancellationToken);
    }

    /// <summary>
    ///     Writes a verified bundle under <c>&lt;EjectRoot&gt;/&lt;catalog&gt;/&lt;timestamp&gt;/</c>.
    /// </summary>
    /// <param name="duckling">The session whose catalog is exported.</param>
    /// <param name="options">Deployment options supplying the eject root and optional signing key.</param>
    /// <param name="includeHistory">
    ///     Whether to also copy the metadata catalog, so snapshots and time travel survive the export.
    ///     The data half is reader-agnostic without it; history requires the catalog.
    /// </param>
    /// <param name="timeProvider">Clock, injected so bundle timestamps are testable.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>Must be called with the session gate already held; see <see cref="RunAsync"/>.</remarks>
    /// <exception cref="InvalidOperationException">A table failed independent verification.</exception>
    public static async Task<CatalogEjectResult> WriteAsync(
        Duckling duckling,
        LakehouseOptions options,
        bool includeHistory,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(duckling);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);

        var catalog = duckling.Catalog;
        var now = timeProvider.GetUtcNow();
        var stamp = now.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var destination = StorageLocation.Combine(
            StorageLocation.Combine(options.EjectRoot, catalog.CatalogName), stamp);
        var remote = StorageLocation.IsRemote(destination);

        StorageLocation.EnsureDirectory(StorageLocation.Combine(destination, DataDirectory));

        var tables = await ExportDataTablesAsync(duckling, destination, remote, cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<BackupTable> metadataTables = [];
        if (includeHistory)
        {
            var catalogDir = StorageLocation.Combine(destination, CatalogDirectory);
            StorageLocation.EnsureDirectory(catalogDir);
            metadataTables = await MetadataExporter.ExportAsync(duckling, catalogDir, cancellationToken)
                .ConfigureAwait(false);
        }

        var (snapshotId, version) = await ReadProvenanceAsync(duckling, catalog.CatalogName, cancellationToken)
            .ConfigureAwait(false);

        // Built unsigned first and signed over itself, so the signature can never cover different
        // values than the manifest carries — the exact drift a reconstructed signing copy invites.
        var signed = !string.IsNullOrEmpty(options.EjectSigningKey);
        var manifest = new EjectManifest
        {
            CatalogName = catalog.CatalogName,
            CreatedUtc = now,
            SnapshotId = snapshotId,
            DuckDbVersion = version,
            IncludesHistory = includeHistory,
            DataTables = tables,
            MetadataTables = metadataTables,
        };

        if (signed)
        {
            manifest = manifest with
            {
                SignatureAlgorithm = EjectSignature.Algorithm,
                Signature = EjectSignature.Compute(manifest, options.EjectSigningKey!),
            };
        }

        await WriteManifestAsync(duckling, destination, remote, manifest, cancellationToken).ConfigureAwait(false);

        return new CatalogEjectResult(
            destination,
            tables.Count,
            tables.Sum(t => t.RowCount),
            Verified: true,
            DigestDeferred: remote && tables.Count > 0,
            IsSigned: signed,
            IncludesHistory: includeHistory);
    }

    /// <summary>
    ///     Exports each user table as clean Parquet, verifying the written file reads back to the same
    ///     row count the catalog reports.
    /// </summary>
    private static async Task<IReadOnlyList<EjectedTable>> ExportDataTablesAsync(
        Duckling duckling,
        string destination,
        bool remote,
        CancellationToken cancellationToken)
    {
        var catalogName = duckling.Catalog.CatalogName;

        // Only base tables in the tenant's own catalog. Views are reconstructable from the metadata
        // catalog, and DuckLake keeps its internal tables in a separate metadata database, so they do
        // not appear here — but the LIKE filter is kept as defence in depth.
        var listed = await duckling
            .ExecuteUnguardedAsync(
                "SELECT schema_name, table_name FROM duckdb_tables() " +
                $"WHERE database_name = {SqlIdentifier.Literal(catalogName)} " +
                "AND table_name NOT LIKE 'ducklake\\_%' ESCAPE '\\' " +
                "ORDER BY schema_name, table_name",
                cancellationToken)
            .ConfigureAwait(false);

        var exported = new List<EjectedTable>();
        foreach (var row in listed.Rows)
        {
            var schema = Convert.ToString(row[0], CultureInfo.InvariantCulture);
            var table = Convert.ToString(row[1], CultureInfo.InvariantCulture);
            if (!SqlIdentifier.IsValid(schema) || !SqlIdentifier.IsValid(table))
            {
                // A name we cannot safely quote as a path segment or an identifier is skipped rather
                // than exported under a mangled name.
                continue;
            }

            var relativePath = $"{DataDirectory}/{schema}/{table}.parquet";
            var target = StorageLocation.Combine(destination, DataDirectory, schema, $"{table}.parquet");
            StorageLocation.EnsureDirectory(StorageLocation.Combine(destination, DataDirectory, schema));

            var qualified = $"\"{catalogName}\".\"{schema}\".\"{table}\"";

            // Re-materialise through the catalog: deletions and updates are applied and the internal
            // columns are dropped, so the output is ordinary Parquet.
            await duckling
                .ExecuteUnguardedAsync(
                    $"COPY (SELECT * FROM {qualified}) TO {SqlIdentifier.Literal(target)} (FORMAT PARQUET)",
                    cancellationToken)
                .ConfigureAwait(false);

            var catalogCount = await ScalarLongAsync(
                duckling, $"SELECT count(*) FROM {qualified}", cancellationToken).ConfigureAwait(false);

            // The verification that makes this an attestation rather than a copy: count the bytes we
            // just wrote, through the plain Parquet reader, and require agreement. read_parquet does
            // not touch DuckLake — this is the same path any downstream reader takes.
            var verifiedCount = await ScalarLongAsync(
                duckling,
                $"SELECT count(*) FROM read_parquet({SqlIdentifier.Literal(target)})",
                cancellationToken).ConfigureAwait(false);

            if (verifiedCount != catalogCount)
            {
                throw new InvalidOperationException(
                    $"Eject verification failed for '{schema}.{table}': the catalog reports " +
                    $"{catalogCount} row(s) but the exported Parquet reads back {verifiedCount}. " +
                    "No manifest was written, so the bundle is marked incomplete.");
            }

            // Byte-level digest for tamper evidence, local only: hashing a remote object would mean
            // downloading it, so remote bundles record a null digest and the result flags it deferred.
            string? sha256 = null;
            long? bytes = null;
            if (!remote)
            {
                sha256 = await ComputeSha256Async(target, cancellationToken).ConfigureAwait(false);
                bytes = new FileInfo(target).Length;
            }

            exported.Add(new EjectedTable(schema, table, relativePath, catalogCount, verifiedCount, sha256, bytes));
        }

        return exported;
    }

    /// <summary>Reads the eject's point-in-time snapshot and the engine version, for provenance.</summary>
    private static async Task<(long? SnapshotId, string? Version)> ReadProvenanceAsync(
        Duckling duckling,
        string catalogName,
        CancellationToken cancellationToken)
    {
        try
        {
            var snap = await duckling
                .ExecuteUnguardedAsync(
                    $"SELECT max(snapshot_id) FROM ducklake_snapshots({SqlIdentifier.Literal(catalogName)})",
                    cancellationToken)
                .ConfigureAwait(false);
            var snapshotId = ToInt64Nullable(snap.Rows.Count > 0 ? snap.Rows[0][0] : null);

            var ver = await duckling.ExecuteUnguardedAsync("SELECT version()", cancellationToken).ConfigureAwait(false);
            var version = ver.Rows.Count > 0 ? Convert.ToString(ver.Rows[0][0], CultureInfo.InvariantCulture) : null;
            return (snapshotId, version);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Provenance is useful but not load-bearing: the table attestation is what a consumer
            // verifies, so a failure to read it must not sink an otherwise complete eject.
            return (null, null);
        }
    }

    /// <summary>
    ///     Writes the completion manifest, always last, so a bundle without it is treated as an
    ///     interrupted export exactly as a backup without its manifest is.
    /// </summary>
    private static async Task WriteManifestAsync(
        Duckling duckling,
        string destination,
        bool remote,
        EjectManifest manifest,
        CancellationToken cancellationToken)
    {
        var path = StorageLocation.Combine(destination, ManifestFileName);

        if (remote)
        {
            // Same constraint as the backup manifest: a remote manifest is written as one unquoted CSV
            // value, which only holds while it contains no newline, so it must be serialised compact.
            var compact = JsonSerializer.Serialize(manifest);
            await duckling
                .ExecuteUnguardedAsync(
                    $"COPY (SELECT {SqlIdentifier.Literal(compact)} AS manifest) TO {SqlIdentifier.Literal(path)} " +
                    "(FORMAT CSV, HEADER false, QUOTE '', DELIMITER '')",
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(manifest, ManifestJson), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Lists eject bundles for a catalog, newest first, over a local eject root.</summary>
    /// <remarks>
    ///     Only the local case is enumerated here. A bundle on an object store has no directory
    ///     listing, and eject targets are operator-configured rather than tenant-supplied, so remote
    ///     listing is left to the storage tooling rather than reimplemented — the write path already
    ///     reports the exact prefix each bundle landed at.
    /// </remarks>
    public static IReadOnlyList<EjectBundle> ListBundles(LakehouseOptions options, string catalogName)
    {
        ArgumentNullException.ThrowIfNull(options);

        var root = StorageLocation.Combine(options.EjectRoot, SqlIdentifier.Quote(catalogName));
        if (StorageLocation.IsRemote(root))
        {
            return [];
        }

        return
        [
            .. StorageLocation.ListChildDirectories(root)
                .Reverse()
                .Select(name =>
                {
                    var location = StorageLocation.Combine(root, name);
                    return new EjectBundle(name, location, TryReadManifest(location));
                }),
        ];
    }

    private static EjectManifest? TryReadManifest(string location)
    {
        var path = Path.Combine(location, ManifestFileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<EjectManifest>(File.ReadAllText(path));
        }
        catch (JsonException)
        {
            // A manifest we cannot parse is treated exactly like a missing one: incomplete.
            return null;
        }
    }

    private static async Task<long> ScalarLongAsync(Duckling duckling, string sql, CancellationToken cancellationToken)
    {
        var result = await duckling.ExecuteUnguardedAsync(sql, cancellationToken).ConfigureAwait(false);
        return ToInt64Nullable(result.Rows.Count > 0 ? result.Rows[0][0] : null) ?? 0;
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    private static long? ToInt64Nullable(object? value) => value switch
    {
        null => null,
        long l => l,
        // Duckling projects wide integers to strings for JSON safety before they reach here.
        string s when long.TryParse(s, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => Convert.ToInt64(value, CultureInfo.InvariantCulture),
    };
}

/// <summary>
///     Deterministic HMAC signing of an eject manifest's load-bearing facts.
/// </summary>
/// <remarks>
///     <para>
///         The signature covers a canonical string built from the attestation — catalog, snapshot,
///         and each table's identity, row counts, and digest — rather than the serialised JSON. That
///         keeps verification independent of JSON formatting: a party recomputing the signature needs
///         only the documented field order and the shared key, not a byte-identical re-serialisation.
///     </para>
///     <para>
///         It is an HMAC, so the same key both signs and verifies. That fits the deployment model,
///         where a lakehold operator hands the key to the party they are proving the export to. It is
///         not a public-key signature and does not claim to be: the point is tamper evidence between
///         two parties who share a secret, not non-repudiation to the world.
///     </para>
/// </remarks>
public static class EjectSignature
{
    /// <summary>Identifier recorded in the manifest's <c>signatureAlgorithm</c> field.</summary>
    public const string Algorithm = "HMAC-SHA256";

    /// <summary>Computes the base64 signature over <paramref name="manifest"/>'s attested facts.</summary>
    public static string Compute(EjectManifest manifest, string key)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrEmpty(key);

        var payload = CanonicalPayload(manifest);
        var signature = HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(payload));
        return Convert.ToBase64String(signature);
    }

    /// <summary>
    ///     Verifies that <paramref name="manifest"/>'s signature matches <paramref name="key"/>.
    /// </summary>
    public static bool Verify(EjectManifest manifest, string key)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        if (string.IsNullOrEmpty(manifest.Signature) || string.IsNullOrEmpty(key))
        {
            return false;
        }

        var expected = Compute(manifest, key);

        // Fixed-time comparison: a signature check that leaks timing is a signature check an attacker
        // can grind against one byte at a time.
        return CryptographicOperations.FixedTimeEquals(
            Convert.FromBase64String(manifest.Signature),
            Convert.FromBase64String(expected));
    }

    private static string CanonicalPayload(EjectManifest manifest)
    {
        var builder = new StringBuilder();
        builder.Append(CultureInfo.InvariantCulture, $"v{manifest.FormatVersion}\n");
        builder.Append(CultureInfo.InvariantCulture, $"catalog={manifest.CatalogName}\n");
        builder.Append(CultureInfo.InvariantCulture, $"snapshot={manifest.SnapshotId?.ToString(CultureInfo.InvariantCulture) ?? "-"}\n");
        builder.Append(CultureInfo.InvariantCulture, $"history={(manifest.IncludesHistory ? 1 : 0)}\n");

        foreach (var table in manifest.DataTables
            .OrderBy(t => t.Schema, StringComparer.Ordinal)
            .ThenBy(t => t.Table, StringComparer.Ordinal))
        {
            builder.Append(CultureInfo.InvariantCulture,
                $"{table.Schema}\t{table.Table}\t{table.RowCount}\t{table.VerifiedRowCount}\t{table.Sha256 ?? "-"}\n");
        }

        return builder.ToString();
    }
}

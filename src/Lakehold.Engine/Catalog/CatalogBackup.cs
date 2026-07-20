using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lakehold.Engine.Configuration;
using Lakehold.Engine.Execution;

namespace Lakehold.Engine.Catalog;

/// <summary>One exported metadata table.</summary>
public sealed record BackupTable(string Name, long RowCount);

/// <summary>
///     Contents of <c>_manifest.json</c>, written last so its presence proves the backup completed.
/// </summary>
public sealed record BackupManifest
{
    /// <summary>Manifest schema version, so a future restore can reject shapes it cannot read.</summary>
    [JsonPropertyName("formatVersion")]
    public int FormatVersion { get; init; } = 1;

    [JsonPropertyName("catalogName")]
    public required string CatalogName { get; init; }

    [JsonPropertyName("createdUtc")]
    public required DateTimeOffset CreatedUtc { get; init; }

    /// <summary>Latest DuckLake snapshot at the time of export, for ordering generations.</summary>
    [JsonPropertyName("snapshotId")]
    public long? SnapshotId { get; init; }

    [JsonPropertyName("duckDbVersion")]
    public string? DuckDbVersion { get; init; }

    /// <summary>
    ///     Where the metadata was read from. Recorded for provenance only: a backup taken from a
    ///     PostgreSQL catalog restores into a local DuckDB file exactly like a local-file one, which
    ///     is what makes the backup an escape hatch from PostgreSQL rather than a copy of it.
    /// </summary>
    [JsonPropertyName("metadataKind")]
    public CatalogMetadataKind MetadataKind { get; init; }

    /// <summary>Schema the metadata tables were read from.</summary>
    [JsonPropertyName("metadataSchema")]
    public string? MetadataSchema { get; init; }

    [JsonPropertyName("tables")]
    public required IReadOnlyList<BackupTable> Tables { get; init; }
}

/// <summary>Outcome of a catalog metadata backup.</summary>
/// <param name="Location">Directory or prefix the backup was written to.</param>
/// <param name="TableCount">Number of metadata tables exported.</param>
/// <param name="Bytes">Total exported size, or null for object stores where listing is not free.</param>
/// <param name="PrunedGenerations">Older generations removed by retention.</param>
/// <param name="RetentionDeferred">
///     True when generations exceeded the retention count but could not be removed. DuckDB can read
///     and write object stores but cannot delete from them, so remote retention needs an external
///     lifecycle policy — reported rather than silently skipped, because an operator who believes
///     retention is running will not notice the bill.
/// </param>
public sealed record CatalogBackupResult(
    string Location,
    int TableCount,
    long? Bytes,
    int PrunedGenerations,
    bool RetentionDeferred = false);

/// <summary>
///     Exports a DuckLake metadata catalog to Parquet so it can be restored independently.
/// </summary>
/// <remarks>
///     <para>
///         DuckLake keeps deletions, file liveness, snapshots, and views in a SQL catalog rather than
///         in manifest files. That makes commits cheap, but the Parquet in your bucket is not
///         self-describing: lose the catalog and a naive rebuild resurrects deleted rows and
///         duplicates updated ones. See <c>docs/EXIT-PATH.md</c>.
///     </para>
///     <para>
///         Backups are written under <see cref="LakehouseOptions.BackupRoot"/>, which is a sibling of
///         the data root and never a child of it. Nesting them inside the data path made them
///         candidates for DuckLake's own orphan cleanup — a backup that deletes itself once it ages
///         is worse than no backup, because it looks like protection.
///     </para>
///     <para>
///         Verified on DuckDB 1.5.3: exporting all 30 metadata tables and rebuilding a catalog from
///         them restored row counts, honoured deletions, preserved updated values, and kept every
///         snapshot — including working <c>AT (VERSION =&gt; n)</c> time travel.
///     </para>
///     <para>
///         Both metadata kinds are supported, and a backup is portable across them: a PostgreSQL
///         catalog exports the same 30 tables and restores into a plain DuckDB file, verified against
///         PostgreSQL 17. That makes this an exit path from the metadata database as well as a
///         backup of it — the catalog database is the one part of a DuckLake deployment that is not
///         already an open format.
///     </para>
/// </remarks>
public static class CatalogBackup
{
    /// <summary>Name of the completion manifest. Its absence marks a backup as unusable.</summary>
    public const string ManifestFileName = "_manifest.json";

    private static readonly JsonSerializerOptions ManifestJson = new() { WriteIndented = true };

    /// <summary>
    ///     Writes every table of the catalog's metadata database to Parquet under
    ///     <c>&lt;BackupRoot&gt;/&lt;catalog&gt;/&lt;timestamp&gt;/</c>, then prunes old generations.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Must be called with the session gate already held — <c>LakehouseMaintenance</c> provides
    ///         it — so no write can commit midway and leave the export describing a state that never
    ///         existed. It uses the unguarded execute path throughout: re-entering the gate would
    ///         deadlock, since it is a plain non-reentrant semaphore.
    ///     </para>
    ///     <para>
    ///         The gate is held for the whole export, which blocks that tenant's queries. Cost is
    ///         proportional to metadata size, not row count — tens of milliseconds on a small catalog,
    ///         but worth scheduling off-peak for a catalog with very many files.
    ///     </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    ///     The catalog's metadata could not be opened for reading.
    /// </exception>
    public static async Task<CatalogBackupResult> WriteAsync(
        Duckling duckling,
        LakehouseOptions options,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(duckling);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);

        var catalog = duckling.Catalog;

        // Timestamped generations rather than overwriting: restoring from a backup taken *after* the
        // corruption you are recovering from turns a recoverable incident into an unrecoverable one.
        var now = timeProvider.GetUtcNow();
        var stamp = now.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var catalogRoot = StorageLocation.Combine(options.BackupRoot, catalog.CatalogName);
        var destination = StorageLocation.Combine(catalogRoot, stamp);
        StorageLocation.EnsureDirectory(destination);

        var source = await OpenMetadataAsync(duckling, cancellationToken).ConfigureAwait(false);
        List<BackupTable> exported;
        try
        {
            exported = await ExportTablesAsync(duckling, source, destination, cancellationToken)
                .ConfigureAwait(false);

            await WriteManifestAsync(duckling, catalog, source, destination, exported, now, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            await source.DisposeAsync().ConfigureAwait(false);
        }

        var (pruned, deferred) = PruneGenerations(catalogRoot, options.BackupRetainCount);

        return new CatalogBackupResult(
            destination,
            exported.Count,
            StorageLocation.TryMeasureBytes(destination),
            pruned,
            deferred);
    }

    /// <summary>Copies every metadata table to Parquet under <paramref name="destination"/>.</summary>
    private static async Task<List<BackupTable>> ExportTablesAsync(
        Duckling duckling,
        MetadataSource source,
        string destination,
        CancellationToken cancellationToken)
    {
        var tables = await duckling
            .ExecuteUnguardedAsync(
                "SELECT table_name FROM duckdb_tables() " +
                $"WHERE database_name = {SqlIdentifier.Literal(source.Alias)} " +
                $"AND schema_name = {SqlIdentifier.Literal(source.Schema)} ORDER BY table_name",
                cancellationToken)
            .ConfigureAwait(false);

        var exported = new List<BackupTable>();
        foreach (var row in tables.Rows)
        {
            var table = Convert.ToString(row[0], CultureInfo.InvariantCulture);
            if (!SqlIdentifier.IsValid(table))
            {
                continue;
            }

            var qualified = $"\"{source.Alias}\".\"{source.Schema}\".\"{table}\"";
            var target = StorageLocation.Combine(destination, $"{table}.parquet");

            await duckling
                .ExecuteUnguardedAsync(
                    $"COPY (SELECT * FROM {qualified}) TO {SqlIdentifier.Literal(target)} (FORMAT PARQUET)",
                    cancellationToken)
                .ConfigureAwait(false);

            // Row counts go in the manifest so a restore can prove it loaded everything, rather
            // than trusting that a file which exists is a file that is complete.
            var counted = await duckling
                .ExecuteUnguardedAsync($"SELECT count(*) FROM {qualified}", cancellationToken)
                .ConfigureAwait(false);

            exported.Add(new BackupTable(table, ToInt64(counted.Rows.Count > 0 ? counted.Rows[0][0] : null)));
        }

        return exported;
    }

    /// <summary>
    ///     A readable handle on the catalog's metadata tables, detaching on dispose if it had to
    ///     attach anything.
    /// </summary>
    private sealed record MetadataSource(Duckling Duckling, string Alias, string Schema, bool Detach)
        : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            if (!Detach)
            {
                return;
            }

            try
            {
                await Duckling.ExecuteUnguardedAsync($"DETACH \"{Alias}\"", CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Leaving the alias attached costs one idle connection until the session is evicted.
                // Failing the backup over it would discard a completed export, which is worse.
            }
        }
    }

    /// <summary>
    ///     Makes the catalog's metadata tables readable, however they are stored.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The two kinds need opposite treatment. For a local file DuckLake has <em>already</em>
    ///         attached the metadata database, and attaching it again fails outright with "Unique file
    ///         handle conflict" — so the alias must be discovered.
    ///     </para>
    ///     <para>
    ///         For PostgreSQL nothing queryable is attached at all: verified on DuckDB 1.5.3, where
    ///         <c>duckdb_databases()</c> reports only the <c>ducklake</c> entry and no metadata
    ///         database behind it. The metadata therefore has to be attached separately, read-only,
    ///         through the postgres extension.
    ///     </para>
    /// </remarks>
    private static async Task<MetadataSource> OpenMetadataAsync(
        Duckling duckling,
        CancellationToken cancellationToken)
    {
        var catalog = duckling.Catalog;

        if (catalog.MetadataKind != CatalogMetadataKind.Postgres)
        {
            var discovered = await ResolveMetadataAliasAsync(duckling, cancellationToken).ConfigureAwait(false);
            return new MetadataSource(duckling, discovered, catalog.ResolvedMetadataSchema, Detach: false);
        }

        var secret = catalog.MetadataSecretName
            ?? throw new InvalidOperationException(
                $"Catalog '{catalog.CatalogName}' stores metadata in PostgreSQL but has no " +
                $"{nameof(CatalogDescriptor.MetadataSecretName)}. Backup reads the metadata tables " +
                "directly, so it needs the name of the DuckDB 'postgres' secret holding their " +
                "credentials — the credentials themselves must not be put on the descriptor.");

        // A fixed alias is safe because the session gate is held for the whole backup, so no second
        // backup can be attaching on this connection concurrently.
        const string alias = "lakehold_backup_meta";

        // Empty target plus SECRET, so no credential is ever interpolated into a statement that
        // DuckDB could echo back inside an error message. READ_ONLY is the other half: this reaches
        // straight past DuckLake into its bookkeeping, and a backup has no business writing there.
        await duckling
            .ExecuteUnguardedAsync(
                $"ATTACH '' AS {alias} (TYPE postgres, SECRET {SqlIdentifier.Quote(secret)}, READ_ONLY)",
                cancellationToken)
            .ConfigureAwait(false);

        return new MetadataSource(duckling, alias, catalog.ResolvedMetadataSchema, Detach: true);
    }

    /// <summary>
    ///     Finds the alias DuckLake attached its own metadata store under.
    /// </summary>
    /// <remarks>
    ///     It is <c>__ducklake_metadata_&lt;catalog&gt;</c> on DuckDB 1.5.3, but that is an internal
    ///     detail. Attaching the file again under our own alias fails outright with
    ///     "Unique file handle conflict", so discovery by path is both necessary and more durable
    ///     than reproducing the naming convention.
    /// </remarks>
    private static async Task<string> ResolveMetadataAliasAsync(Duckling duckling, CancellationToken cancellationToken)
    {
        var catalogName = duckling.Catalog.CatalogName;

        var databases = await duckling
            .ExecuteUnguardedAsync(
                "SELECT database_name, path FROM duckdb_databases() WHERE type = 'duckdb'",
                cancellationToken)
            .ConfigureAwait(false);

        var candidates = databases.Rows
            .Select(r => (
                Name: Convert.ToString(r[0], CultureInfo.InvariantCulture) ?? string.Empty,
                Path: Convert.ToString(r[1], CultureInfo.InvariantCulture)))
            .Where(d => SqlIdentifier.IsValid(d.Name))
            .ToArray();

        // Path equality first, but it cannot be the only rule: DuckDB reports the resolved path, and
        // on macOS a temp directory arrives as /var/folders/… while resolving to /private/var/… .
        // Matching only on the string made backup fail everywhere temp paths are symlinked.
        var byPath = candidates.FirstOrDefault(d =>
            d.Path is not null && PathsMatch(d.Path, duckling.Catalog.MetadataSource));
        if (byPath.Name is { Length: > 0 })
        {
            return byPath.Name;
        }

        // DuckLake's own convention on DuckDB 1.5.3. An internal detail, so it is a fallback rather
        // than the primary rule.
        var byConvention = candidates.FirstOrDefault(d =>
            d.Name.StartsWith("__ducklake_metadata_", StringComparison.Ordinal) &&
            d.Name.EndsWith(catalogName, StringComparison.Ordinal));
        if (byConvention.Name is { Length: > 0 })
        {
            return byConvention.Name;
        }

        // Last resort: the only file-backed database that is not one of DuckDB's built-ins.
        var remaining = candidates
            .Where(d => d.Path is { Length: > 0 })
            .Where(d => !string.Equals(d.Name, "memory", StringComparison.Ordinal)
                     && !string.Equals(d.Name, "system", StringComparison.Ordinal)
                     && !string.Equals(d.Name, "temp", StringComparison.Ordinal))
            .ToArray();

        return remaining.Length == 1
            ? remaining[0].Name
            : throw new InvalidOperationException(
                $"Could not locate the attached metadata database for catalog '{catalogName}'. " +
                $"Candidates: {string.Join(", ", candidates.Select(c => c.Name))}.");
    }

    /// <summary>Compares two filesystem paths, tolerating symlinked roots and relative forms.</summary>
    private static bool PathsMatch(string left, string right)
    {
        if (string.Equals(left, right, StringComparison.Ordinal))
        {
            return true;
        }

        try
        {
            var l = Path.GetFullPath(left);
            var r = Path.GetFullPath(right);
            return string.Equals(l, r, StringComparison.Ordinal)
                || string.Equals(ResolveLinks(l), ResolveLinks(r), StringComparison.Ordinal);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string ResolveLinks(string path)
    {
        try
        {
            return File.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName ?? path;
        }
        catch (IOException)
        {
            return path;
        }
    }

    /// <summary>
    ///     Writes the completion manifest. Always last — its presence is what distinguishes a
    ///     finished backup from one that died partway through and would restore a broken catalog.
    /// </summary>
    private static async Task WriteManifestAsync(
        Duckling duckling,
        CatalogDescriptor catalog,
        MetadataSource source,
        string destination,
        IReadOnlyList<BackupTable> tables,
        DateTimeOffset createdUtc,
        CancellationToken cancellationToken)
    {
        long? snapshotId = null;
        string? version = null;

        try
        {
            var snap = await duckling
                .ExecuteUnguardedAsync(
                    $"SELECT max(snapshot_id) FROM ducklake_snapshots({SqlIdentifier.Literal(catalog.CatalogName)})",
                    cancellationToken)
                .ConfigureAwait(false);
            snapshotId = ToInt64Nullable(snap.Rows.Count > 0 ? snap.Rows[0][0] : null);

            var ver = await duckling.ExecuteUnguardedAsync("SELECT version()", cancellationToken).ConfigureAwait(false);
            version = ver.Rows.Count > 0 ? Convert.ToString(ver.Rows[0][0], CultureInfo.InvariantCulture) : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Provenance is useful but not load-bearing; the table list is what restore validates.
        }

        var manifest = new BackupManifest
        {
            CatalogName = catalog.CatalogName,
            CreatedUtc = createdUtc,
            SnapshotId = snapshotId,
            DuckDbVersion = version,
            MetadataKind = catalog.MetadataKind,
            MetadataSchema = source.Schema,
            Tables = tables,
        };

        var path = StorageLocation.Combine(destination, ManifestFileName);
        if (StorageLocation.IsRemote(destination))
        {
            // No local filesystem to write through, so the JSON goes out as a single unquoted CSV
            // value. That only holds while the value contains no newline, so remote manifests are
            // serialised compact — indenting one would split it across rows and write a file that
            // parses back as garbage, which restore would then read as "incomplete".
            var compact = JsonSerializer.Serialize(manifest);
            await duckling
                .ExecuteUnguardedAsync(
                    $"COPY (SELECT {SqlIdentifier.Literal(compact)} AS manifest) TO {SqlIdentifier.Literal(path)} " +
                    "(FORMAT CSV, HEADER false, QUOTE '', DELIMITER '')",
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(manifest, ManifestJson), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    ///     Removes the oldest generations beyond <paramref name="retain"/>.
    /// </summary>
    /// <remarks>
    ///     Timestamped names sort chronologically as strings, so ordinal ordering is enough. Only
    ///     generations carrying a manifest are eligible for deletion — an incomplete backup is left
    ///     alone for an operator to look at rather than quietly tidied away.
    /// </remarks>
    private static (int Pruned, bool Deferred) PruneGenerations(string catalogRoot, int retain)
    {
        if (retain <= 0)
        {
            return (0, false);
        }

        if (StorageLocation.IsRemote(catalogRoot))
        {
            // DuckDB can read and write object stores but has no delete, so pruning a remote backup
            // root needs an external lifecycle rule. Reported rather than swallowed: an operator who
            // thinks retention is running will not go looking for the growing bill.
            return (0, true);
        }

        var generations = StorageLocation.ListChildDirectories(catalogRoot);
        if (generations.Count <= retain)
        {
            return (0, false);
        }

        var pruned = 0;
        foreach (var name in generations.Take(generations.Count - retain))
        {
            var dir = Path.Combine(catalogRoot, name);
            if (!File.Exists(Path.Combine(dir, ManifestFileName)))
            {
                continue;
            }

            try
            {
                Directory.Delete(dir, recursive: true);
                pruned++;
            }
            catch (IOException)
            {
                // A generation we cannot remove is not worth failing an otherwise good backup over.
            }
        }

        return (pruned, false);
    }

    private static long ToInt64(object? value) => ToInt64Nullable(value) ?? 0;

    private static long? ToInt64Nullable(object? value) => value switch
    {
        null => null,
        long l => l,
        // Duckling projects wide integers to strings for JSON safety before they reach here.
        string s when long.TryParse(s, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => Convert.ToInt64(value, CultureInfo.InvariantCulture),
    };
}

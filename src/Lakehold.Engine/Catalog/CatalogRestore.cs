using System.Globalization;
using System.Text.Json;
using DuckDB.EFCoreProvider.Extensions;
using DuckDB.EFCoreProvider.Infrastructure;
using Lakehold.Engine.Configuration;
using Lakehold.Engine.Execution;
using Microsoft.EntityFrameworkCore;

namespace Lakehold.Engine.Catalog;

/// <summary>A backup generation available to restore.</summary>
/// <param name="Generation">Timestamped directory name.</param>
/// <param name="Location">Full path or prefix.</param>
/// <param name="Manifest">Parsed manifest, or null when the generation is incomplete.</param>
public sealed record BackupGeneration(string Generation, string Location, BackupManifest? Manifest)
{
    /// <summary>Whether this generation completed and is safe to restore from.</summary>
    public bool IsComplete => Manifest is not null;
}

/// <summary>Outcome of a restore.</summary>
public sealed record CatalogRestoreResult(
    string MetadataPath,
    string Generation,
    int TablesRestored,
    long RowsRestored);

/// <summary>
///     Rebuilds a DuckLake metadata catalog from a Parquet backup.
/// </summary>
/// <remarks>
///     <para>
///         Restore always writes a <em>new</em> metadata file and refuses to overwrite an existing
///         one. Recovery happens under pressure, and an operation that can clobber the catalog you
///         are trying to rescue is the wrong shape. Re-pointing the tenant at the restored file is a
///         separate, deliberate step.
///     </para>
///     <para>
///         It runs on its own connection rather than inside a <see cref="Duckling"/>: the session for
///         a catalog has that catalog's metadata attached, and the whole point here is that the
///         original may be gone.
///     </para>
/// </remarks>
public static class CatalogRestore
{
    /// <summary>Lists backup generations for a catalog, newest first.</summary>
    /// <param name="options">Deployment options supplying the backup root.</param>
    /// <param name="catalogName">Catalog whose generations to list.</param>
    /// <param name="configure">
    ///     Provider configuration used only when the backup root is an object store, where listing
    ///     needs a DuckDB connection with the bucket's credentials. Credentials belong here, in
    ///     connection configuration, and never in <paramref name="options"/>.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task<IReadOnlyList<BackupGeneration>> ListGenerationsAsync(
        LakehouseOptions options,
        string catalogName,
        Action<DuckDBDbContextOptionsBuilder>? configure,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        var root = StorageLocation.Combine(options.BackupRoot, SqlIdentifier.Quote(catalogName));

        if (StorageLocation.IsRemote(root))
        {
            return await ListRemoteGenerationsAsync(root, configure, cancellationToken).ConfigureAwait(false);
        }

        return
        [
            .. StorageLocation.ListChildDirectories(root)
                .Reverse()
                .Select(name =>
                {
                    var location = StorageLocation.Combine(root, name);
                    return new BackupGeneration(name, location, TryReadLocalManifest(location));
                }),
        ];
    }

    /// <summary>
    ///     Lists generations held in an object store.
    /// </summary>
    /// <remarks>
    ///     Object stores have no directories to enumerate, so generations are inferred from the keys
    ///     of the objects inside them via <c>glob</c>, and manifests are pulled in one pass with
    ///     <c>read_text</c> over a glob rather than one request per generation. Verified against
    ///     S3-compatible storage on DuckDB 1.5.3.
    /// </remarks>
    private static async Task<IReadOnlyList<BackupGeneration>> ListRemoteGenerationsAsync(
        string root,
        Action<DuckDBDbContextOptionsBuilder>? configure,
        CancellationToken cancellationToken)
    {
        await using var context = CreateStandaloneContext(configure);
        await context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var files = await ReadColumnAsync(
            context, $"SELECT file FROM glob({SqlIdentifier.Literal($"{root}/*/*")})", cancellationToken)
            .ConfigureAwait(false);

        // A generation is the prefix segment directly above each object.
        var generations = files
            .Select(f => f.Split('/'))
            .Where(parts => parts.Length >= 2)
            .Select(parts => parts[^2])
            .Distinct(StringComparer.Ordinal)
            .OrderDescending(StringComparer.Ordinal)
            .ToArray();

        var manifests = await ReadRemoteManifestsAsync(root, context, cancellationToken).ConfigureAwait(false);

        return
        [
            .. generations.Select(name => new BackupGeneration(
                name,
                StorageLocation.Combine(root, name),
                manifests.GetValueOrDefault(name))),
        ];
    }

    private static async Task<Dictionary<string, BackupManifest>> ReadRemoteManifestsAsync(
        string root,
        LakeContext context,
        CancellationToken cancellationToken)
    {
        var found = new Dictionary<string, BackupManifest>(StringComparer.Ordinal);
        var pattern = SqlIdentifier.Literal($"{root}/*/{CatalogBackup.ManifestFileName}");

        List<(string File, string Content)> rows;
        try
        {
            rows = await ReadPairsAsync(
                context, $"SELECT filename, content FROM read_text({pattern})", cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // DuckDB raises rather than returning empty when a glob matches nothing, which is the
            // ordinary state of a backup root holding only interrupted generations.
            return found;
        }

        foreach (var (file, content) in rows)
        {
            var parts = file.Split('/');
            if (parts.Length < 2)
            {
                continue;
            }

            try
            {
                if (JsonSerializer.Deserialize<BackupManifest>(content) is { } manifest)
                {
                    found[parts[^2]] = manifest;
                }
            }
            catch (JsonException)
            {
                // Treated exactly like a missing manifest: not restorable.
            }
        }

        return found;
    }

    /// <summary>
    ///     Rebuilds a metadata catalog from <paramref name="generation"/> into
    ///     <paramref name="targetMetadataPath"/>, then verifies it attaches and reads.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    ///     The generation has no manifest (so it may be a partial backup), the target already
    ///     exists, or verification failed.
    /// </exception>
    public static async Task<CatalogRestoreResult> RestoreAsync(
        LakehouseOptions options,
        string catalogName,
        string? generation,
        string targetMetadataPath,
        string dataPath,
        CancellationToken cancellationToken,
        Action<DuckDBDbContextOptionsBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetMetadataPath);

        var candidates = await ListGenerationsAsync(options, catalogName, configure, cancellationToken)
            .ConfigureAwait(false);
        var chosen = generation is null
            ? candidates.FirstOrDefault(g => g.IsComplete)
            : candidates.FirstOrDefault(g => g.Generation == generation);

        if (chosen is null)
        {
            throw new InvalidOperationException(
                generation is null
                    ? $"No complete backup generation found for catalog '{catalogName}'."
                    : $"Backup generation '{generation}' was not found for catalog '{catalogName}'.");
        }

        // A generation without a manifest died partway through. Restoring it would produce a catalog
        // that looks fine and is missing tables — if the missing one is ducklake_delete_file, deleted
        // rows silently return, which is the exact failure this whole feature exists to prevent.
        var manifest = chosen.Manifest
            ?? throw new InvalidOperationException(
                $"Backup generation '{chosen.Generation}' has no {CatalogBackup.ManifestFileName} and is " +
                "therefore incomplete. Restoring it could produce a catalog that silently omits deletions.");

        if (File.Exists(targetMetadataPath))
        {
            throw new InvalidOperationException(
                $"'{targetMetadataPath}' already exists. Restore never overwrites an existing catalog; " +
                "choose a new path and re-point the tenant once you have verified the result.");
        }

        var directory = Path.GetDirectoryName(targetMetadataPath);
        if (!string.IsNullOrEmpty(directory))
        {
            StorageLocation.EnsureDirectory(directory);
        }

        await using var context = CreateStandaloneContext(configure);
        await context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        async Task Exec(string sql)
        {
            await using var r = await context.Database.SqlQueryDynamicRawAsync(sql, cancellationToken).ConfigureAwait(false);
            await foreach (var _ in r.ReadRowsAsync(cancellationToken).ConfigureAwait(false))
            {
                // Drain so the statement completes.
            }
        }

        await Exec($"ATTACH {SqlIdentifier.Literal(targetMetadataPath)} AS restored");

        long rows = 0;
        foreach (var table in manifest.Tables)
        {
            var source = StorageLocation.Combine(chosen.Location, $"{table.Name}.parquet");
            await Exec(
                $"CREATE TABLE restored.main.\"{SqlIdentifier.Quote(table.Name)}\" AS " +
                $"SELECT * FROM read_parquet({SqlIdentifier.Literal(source)})");
            rows += table.RowCount;
        }

        await Exec("DETACH restored");

        await VerifyAsync(context, targetMetadataPath, dataPath, manifest, Exec).ConfigureAwait(false);

        return new CatalogRestoreResult(targetMetadataPath, chosen.Generation, manifest.Tables.Count, rows);
    }

    /// <summary>
    ///     Attaches the rebuilt catalog and reads from it, so a restore that produced an unusable
    ///     catalog fails here rather than at the operator's next query.
    /// </summary>
    private static async Task VerifyAsync(
        LakeContext context,
        string metadataPath,
        string dataPath,
        BackupManifest manifest,
        Func<string, Task> exec)
    {
        try
        {
            await exec(
                $"ATTACH 'ducklake:{metadataPath}' AS verify (DATA_PATH {SqlIdentifier.Literal(dataPath)}, READ_ONLY)");
            await exec($"SELECT count(*) FROM ducklake_snapshots('verify')");
            await exec("DETACH verify");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Restored catalog at '{metadataPath}' did not attach cleanly: {ex.Message}. " +
                $"The backup generation may be corrupt (manifest reports {manifest.Tables.Count} tables).",
                ex);
        }
    }

    private static LakeContext CreateStandaloneContext(Action<DuckDBDbContextOptionsBuilder>? configure)
    {
        var builder = new DbContextOptionsBuilder<LakeContext>();
        builder.UseDuckDB(
            "Data Source=:memory:",
            duckDb =>
            {
                duckDb.LoadExtension("ducklake");
                duckDb.LoadExtension("httpfs");

                // Where an object-store secret is installed, so a bucket-hosted backup can be listed
                // and read without the credential ever touching options or the catalog record.
                configure?.Invoke(duckDb);
            });

        return new LakeContext(builder.Options);
    }

    /// <summary>Runs <paramref name="sql"/> and returns its first column as strings.</summary>
    private static async Task<List<string>> ReadColumnAsync(
        LakeContext context,
        string sql,
        CancellationToken cancellationToken)
    {
        var values = new List<string>();
        await using var reader = await context.Database
            .SqlQueryDynamicRawAsync(sql, cancellationToken).ConfigureAwait(false);

        await foreach (var row in reader.ReadRowsAsync(cancellationToken).ConfigureAwait(false))
        {
            if (Convert.ToString(row.Span[0], CultureInfo.InvariantCulture) is { Length: > 0 } value)
            {
                values.Add(value);
            }
        }

        return values;
    }

    /// <summary>Runs <paramref name="sql"/> and returns its first two columns as strings.</summary>
    private static async Task<List<(string First, string Second)>> ReadPairsAsync(
        LakeContext context,
        string sql,
        CancellationToken cancellationToken)
    {
        var values = new List<(string, string)>();
        await using var reader = await context.Database
            .SqlQueryDynamicRawAsync(sql, cancellationToken).ConfigureAwait(false);

        await foreach (var row in reader.ReadRowsAsync(cancellationToken).ConfigureAwait(false))
        {
            var span = row.Span;
            values.Add((
                Convert.ToString(span[0], CultureInfo.InvariantCulture) ?? string.Empty,
                Convert.ToString(span[1], CultureInfo.InvariantCulture) ?? string.Empty));
        }

        return values;
    }

    private static BackupManifest? TryReadLocalManifest(string location)
    {
        var path = Path.Combine(location, CatalogBackup.ManifestFileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(path));
        }
        catch (JsonException)
        {
            // A manifest we cannot parse is treated exactly like a missing one: not restorable.
            return null;
        }
    }
}

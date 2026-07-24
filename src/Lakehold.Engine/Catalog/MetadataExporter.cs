using System.Globalization;
using DuckDB.NET.Data;
using Lakehold.Engine.Execution;

namespace Lakehold.Engine.Catalog;

/// <summary>
///     Copies a catalog's DuckLake metadata tables to Parquet, wherever the metadata is stored.
/// </summary>
/// <remarks>
///     <para>
///         DuckLake keeps deletions, file liveness, snapshots, and views in a SQL catalog rather
///         than in manifest files. That catalog is the one part of a DuckLake deployment that is not
///         already an open format, so copying it out is what makes a backup — or an eject bundle —
///         restorable without the original metadata database.
///     </para>
///     <para>
///         Both <see cref="CatalogBackup"/> (writing a timestamped generation) and
///         <see cref="CatalogEject"/> (writing the history half of a portable bundle) need exactly
///         this copy, differing only in where the files land and what manifest wraps them. The copy
///         itself — discovering how the metadata is attached, listing its tables, and exporting each
///         with a row count — lives here so both share one implementation and one set of hard-won
///         edge cases (symlinked temp roots, PostgreSQL metadata that is not attached at all).
///     </para>
///     <para>
///         Every method uses the unguarded execute path and must therefore be called with the
///         session gate already held: the gate is a non-reentrant semaphore, so re-entering it would
///         deadlock. Holding it for the whole export is also what stops a write committing midway and
///         leaving the copy describing a state that never existed.
///     </para>
/// </remarks>
public static class MetadataExporter
{
    /// <summary>
    ///     Exports every metadata table of <paramref name="duckling"/>'s catalog to Parquet under
    ///     <paramref name="destination"/>, returning the table list with row counts.
    /// </summary>
    /// <remarks>
    ///     Row counts are returned so the caller's manifest can prove a later restore loaded
    ///     everything, rather than trusting that a file which exists is a file that is complete.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    ///     The catalog's metadata could not be opened for reading.
    /// </exception>
    public static async Task<IReadOnlyList<BackupTable>> ExportAsync(
        Duckling duckling,
        string destination,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(duckling);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);

        var source = await OpenMetadataAsync(duckling, cancellationToken).ConfigureAwait(false);
        try
        {
            return await ExportTablesAsync(duckling, source, destination, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await source.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Copies every metadata table to Parquet under <paramref name="destination"/>.</summary>
    private static async Task<List<BackupTable>> ExportTablesAsync(
        Duckling duckling,
        MetadataSource source,
        string destination,
        CancellationToken cancellationToken)
    {
        var tableNames = await ListMetadataTablesAsync(duckling, source, cancellationToken).ConfigureAwait(false);

        // A DuckLake catalog always has metadata tables, so an empty list means discovery failed
        // rather than that there was nothing to copy. Failing here is the whole point: a manifest
        // reporting zero tables is an empty backup that still presents itself as complete, which is
        // exactly what invariants 12 and 16 exist to prevent.
        if (tableNames.Count == 0)
        {
            throw new InvalidOperationException(
                $"No metadata tables were found for catalog '{duckling.Catalog.CatalogName}' via alias " +
                $"'{source.Alias}' (schema '{source.Schema}'). Refusing to write a backup that would " +
                "claim to be complete while containing nothing.");
        }

        var exported = new List<BackupTable>();
        foreach (var table in tableNames)
        {
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

            // The count comes from the Parquet footer just written, not a second scan of the source:
            // the footer already records the row count, so this is a metadata read rather than
            // another full pass over every metadata table.
            var counted = await duckling
                .ExecuteUnguardedAsync(
                    $"SELECT num_rows FROM parquet_file_metadata({SqlIdentifier.Literal(target)})",
                    cancellationToken)
                .ConfigureAwait(false);

            exported.Add(new BackupTable(table, ToInt64(counted.Rows.Count > 0 ? counted.Rows[0][0] : null)));
        }

        return exported;
    }

    /// <summary>
    ///     Lists the metadata tables to copy.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The ordinary route is <c>duckdb_tables()</c> on the session itself, which is correct for
    ///         PostgreSQL metadata — attached explicitly, and therefore visible — and for DuckDB 1.5.3.
    ///     </para>
    ///     <para>
    ///         From DuckDB 1.5.4, a local-file metadata catalog is attached but hidden from every
    ///         introspection surface: <c>duckdb_databases()</c>, <c>duckdb_tables()</c>,
    ///         <c>PRAGMA database_list</c>, and the alias's own <c>information_schema</c> all omit it,
    ///         while its tables remain directly readable. Enumeration then happens over a second,
    ///         read-only connection to the metadata file, where it is an ordinary DuckDB database and
    ///         lists normally.
    ///     </para>
    ///     <para>
    ///         The list must be discovered rather than hard-coded, and inlined data is why. DuckLake
    ///         stages a small commit in a per-table <c>ducklake_inlined_data_&lt;schema&gt;_&lt;table&gt;</c>
    ///         table whose name depends on ids that only exist at run time. Those rows are committed
    ///         data that is not yet in Parquet, so a fixed table list would omit them and produce a
    ///         backup that silently loses the newest writes — the failure the open-format guarantee is
    ///         written against.
    ///     </para>
    /// </remarks>
    private static async Task<List<string>> ListMetadataTablesAsync(
        Duckling duckling,
        MetadataSource source,
        CancellationToken cancellationToken)
    {
        var listed = await duckling
            .ExecuteUnguardedAsync(
                "SELECT table_name FROM duckdb_tables() " +
                $"WHERE database_name = {SqlIdentifier.Literal(source.Alias)} " +
                $"AND schema_name = {SqlIdentifier.Literal(source.Schema)} ORDER BY table_name",
                cancellationToken)
            .ConfigureAwait(false);

        var names = listed.Rows
            .Select(r => Convert.ToString(r[0], CultureInfo.InvariantCulture))
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .ToList();

        if (names.Count > 0 || duckling.Catalog.MetadataKind == CatalogMetadataKind.Postgres)
        {
            return names;
        }

        return await ListViaSideConnectionAsync(duckling.Catalog.MetadataSource, source.Schema, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    ///     Lists a metadata file's tables over an independent read-only connection.
    /// </summary>
    /// <remarks>
    ///     Read-only, so this cannot alter the catalog it is inspecting, and consistent because the
    ///     caller holds the session gate for the whole export — no write can land between this listing
    ///     and the copies that follow. The copies still run on the session's own connection, which is
    ///     where object-store credentials live: enumerating here and exporting there keeps a backup
    ///     written to S3 working, which a second connection would have no secrets for.
    /// </remarks>
    private static async Task<List<string>> ListViaSideConnectionAsync(
        string metadataPath,
        string schema,
        CancellationToken cancellationToken)
    {
        const string alias = "lakehold_metadata_scan";

        await using var connection = new DuckDBConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using (var attach = connection.CreateCommand())
        {
            attach.CommandText =
                $"ATTACH {SqlIdentifier.Literal(metadataPath)} AS {SqlIdentifier.Quote(alias, nameof(alias))} (READ_ONLY)";
            await attach.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT table_name FROM duckdb_tables() " +
            $"WHERE database_name = {SqlIdentifier.Literal(alias)} " +
            $"AND schema_name = {SqlIdentifier.Literal(schema)} ORDER BY table_name";

        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (reader.GetValue(0) is string name && name.Length > 0)
            {
                names.Add(name);
            }
        }

        return names;
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
                // Failing the export over it would discard completed work, which is worse.
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
                $"{nameof(CatalogDescriptor.MetadataSecretName)}. Metadata export reads the metadata tables " +
                "directly, so it needs the name of the DuckDB 'postgres' secret holding their " +
                "credentials — the credentials themselves must not be put on the descriptor.");

        // A fixed alias is safe because the session gate is held for the whole export, so no second
        // export can be attaching on this connection concurrently.
        const string alias = "lakehold_export_meta";

        // Empty target plus SECRET, so no credential is ever interpolated into a statement that
        // DuckDB could echo back inside an error message. READ_ONLY is the other half: this reaches
        // straight past DuckLake into its bookkeeping, and an export has no business writing there.
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
    ///     <para>
    ///         The alias is <c>__ducklake_metadata_&lt;catalog&gt;</c>. Attaching the file again under
    ///         our own name fails outright with "Unique file handle conflict", so the existing
    ///         attachment has to be found rather than recreated.
    ///     </para>
    ///     <para>
    ///         Two ways of finding it, in that order, because each covers the other's blind spot.
    ///         <em>Probing the conventional name</em> is tried first: it is the only method that works
    ///         on DuckDB 1.5.4, where the metadata database is still attached under exactly that name
    ///         but is no longer listed by <c>duckdb_databases()</c> at all. Discovery through that
    ///         listing is the fallback, covering any future release that keeps listing the database
    ///         but renames it.
    ///     </para>
    ///     <para>
    ///         The order was originally the reverse, on the reasoning that the naming convention is an
    ///         internal detail and a path match is more durable. DuckDB 1.5.4 inverted that: the
    ///         convention held exactly, and the listing is what disappeared — taking backup, restore,
    ///         and eject with it, since all three read the metadata tables directly.
    ///     </para>
    /// </remarks>
    private static async Task<string> ResolveMetadataAliasAsync(Duckling duckling, CancellationToken cancellationToken)
    {
        var catalogName = duckling.Catalog.CatalogName;

        // The conventional alias, confirmed by reading from it. A probe is what makes this safe to
        // try first: if the name is wrong the query fails, costs nothing, and discovery still runs.
        var conventional = "__ducklake_metadata_" + catalogName;
        if (SqlIdentifier.IsValid(conventional)
            && await IsMetadataDatabaseAsync(duckling, conventional, cancellationToken).ConfigureAwait(false))
        {
            return conventional;
        }

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

        // Path equality, which cannot be an exact-string rule: DuckDB reports the resolved path, and
        // on macOS a temp directory arrives as /var/folders/… while resolving to /private/var/… .
        // Matching only on the string made export fail everywhere temp paths are symlinked.
        var byPath = candidates.FirstOrDefault(d =>
            d.Path is not null && PathsMatch(d.Path, duckling.Catalog.MetadataSource));
        if (byPath.Name is { Length: > 0 })
        {
            return byPath.Name;
        }

        // A listed database following the convention, for a catalog name the probe above could not
        // form a valid identifier from.
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
                $"Tried the conventional alias '{conventional}' and every attached database. " +
                $"Candidates: {string.Join(", ", candidates.Select(c => c.Name))}.");
    }

    /// <summary>
    ///     Whether <paramref name="alias"/> names an attached DuckLake metadata database.
    /// </summary>
    /// <remarks>
    ///     Reads one of DuckLake's own bookkeeping tables rather than merely checking the name
    ///     resolves, so an unrelated database that happened to match the convention cannot be mistaken
    ///     for the metadata store. A failure means "not this one", never an error worth propagating —
    ///     the caller has other strategies to try.
    /// </remarks>
    private static async Task<bool> IsMetadataDatabaseAsync(
        Duckling duckling,
        string alias,
        CancellationToken cancellationToken)
    {
        try
        {
            var schema = SqlIdentifier.Quote(duckling.Catalog.ResolvedMetadataSchema, "metadataSchema");
            await duckling
                .ExecuteUnguardedAsync(
                    $"SELECT 1 FROM {SqlIdentifier.Quote(alias, nameof(alias))}.{schema}.\"ducklake_snapshot\" LIMIT 1",
                    cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
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

    private static long ToInt64(object? value) => value switch
    {
        null => 0,
        long l => l,
        // Duckling projects wide integers to strings for JSON safety before they reach here.
        string s when long.TryParse(s, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => Convert.ToInt64(value, CultureInfo.InvariantCulture),
    };
}

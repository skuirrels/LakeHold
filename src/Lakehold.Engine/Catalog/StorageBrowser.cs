using System.Globalization;
using Lakehold.Engine.Execution;

namespace Lakehold.Engine.Catalog;

/// <summary>
///     The physical footprint of one table: what it weighs, how many Parquet files it is spread
///     across, and how much of it has not been written out yet.
/// </summary>
/// <param name="SchemaName">Schema the table belongs to.</param>
/// <param name="TableName">Table name, unqualified.</param>
/// <param name="RowCount">
///     Live rows — the number a <c>SELECT count(*)</c> would return. Merge-on-read deletes are
///     already subtracted, and rows still inlined in the metadata catalog are already included.
/// </param>
/// <param name="InlinedRows">
///     Rows committed but not yet written to Parquet. Non-zero means <c>flush</c> has work to do,
///     and is the only thing distinguishing a table whose data is entirely inlined from an empty
///     one — both report zero files.
/// </param>
/// <param name="FileCount">Live Parquet data files.</param>
/// <param name="FileSizeBytes">Total size of those files.</param>
/// <param name="DeleteFileCount">Live merge-on-read delete files.</param>
/// <param name="DeleteFileSizeBytes">Total size of those delete files.</param>
public sealed record TableStorageInfo(
    string SchemaName,
    string TableName,
    long RowCount,
    long InlinedRows,
    long FileCount,
    long FileSizeBytes,
    long DeleteFileCount,
    long DeleteFileSizeBytes)
{
    /// <summary>
    ///     Mean bytes per data file, or null when the table has no files. The number the fragmentation
    ///     advisory is drawn from; the threshold it is compared against lives in the API layer, not here.
    /// </summary>
    public long? AverageFileSizeBytes => FileCount > 0 ? FileSizeBytes / FileCount : null;
}

/// <summary>A catalog's storage footprint, table by table.</summary>
/// <param name="Tables">One entry per user table, ordered by schema then name.</param>
/// <param name="TargetFileSizeBytes">
///     The catalog's configured <c>target_file_size</c>, or null when it has never been set and
///     DuckLake's built-in default applies. Null is reported rather than guessed: the built-in
///     default is not exposed through any setting or metadata row, so inventing a number here would
///     make the fragmentation advisory a fiction dressed as a measurement.
/// </param>
public sealed record CatalogStorageInfo(
    IReadOnlyList<TableStorageInfo> Tables,
    long? TargetFileSizeBytes);

/// <summary>One Parquet data file, with the delete file paired to it when it has one.</summary>
/// <param name="DataFile">Path as the catalog records it — a local path or an object-store URI.</param>
/// <param name="DataFileSizeBytes">Size of the data file.</param>
/// <param name="DeleteFile">
///     The merge-on-read delete file applying to this data file, or null when it has none. Its
///     presence means a reader is paying to skip rows that are still on disk.
/// </param>
/// <param name="DeleteFileSizeBytes">Size of that delete file, or null when there is none.</param>
public sealed record DataFileInfo(
    string DataFile,
    long DataFileSizeBytes,
    string? DeleteFile,
    long? DeleteFileSizeBytes);

/// <summary>A table's data files at one snapshot.</summary>
/// <param name="Truncated">
///     Whether the list hit <c>maxRows</c> and stops short of the table's real file count. Follows
///     the shape the change feed already uses rather than inventing the cursor pagination
///     <c>docs/PUBLIC-API.md</c> plans, so all these surfaces convert together.
/// </param>
public sealed record TableFileList(
    string SchemaName,
    string TableName,
    long? SnapshotId,
    bool Truncated,
    IReadOnlyList<DataFileInfo> Files);

/// <summary>
///     Reads a catalog's physical layout — table sizes, Parquet file counts, and delete-file
///     overhead — so an operator can tell whether the maintenance operations are worth running.
/// </summary>
/// <remarks>
///     <para>
///         The figures come from DuckLake's own catalog, never from listing the data path. Anything
///         under that path the catalog does not reference is orphan-cleanup fodder (invariant 11),
///         and superseded update rows and merge-on-read deletes mean the bytes on disk are not the
///         rows in the table (invariant 15) — so a directory listing would present garbage and live
///         data identically. Enumerating an object store is also the unbounded <c>LIST</c> that times
///         out at scale upstream (ducklake#1090). See <c>docs/UI.md</c>.
///     </para>
///     <para>
///         Two sources, joined in one statement because per-table round trips make the cost scale
///         with catalog size — the same reasoning as <see cref="CatalogBrowser"/>:
///     </para>
///     <list type="bullet">
///         <item>
///             <description>
///                 <c>ducklake_table_info</c>, a supported table function, for the file figures. It
///                 reports only user tables, so unlike <c>information_schema</c> it needs no filtering
///                 for DuckLake's own metadata tables.
///             </description>
///         </item>
///         <item>
///             <description>
///                 The metadata catalog, for row counts and schema names, which the table function
///                 does not carry. This is the same coupling <see cref="MetadataExporter"/> already
///                 has, and it is why <see cref="MetadataExporter.ResolveMetadataAliasAsync"/> is
///                 shared rather than reimplemented: exactly one place knows how DuckLake attaches
///                 its own metadata.
///             </description>
///         </item>
///     </list>
///     <para>
///         Verified against DuckLake on DuckDB 1.5.5, not read from documentation. The figures below
///         were checked against ground truth on a catalog with inlined rows, flushed rows, and
///         merge-on-read deletes:
///     </para>
///     <list type="number">
///         <item>
///             <description>
///                 <c>ducklake_table_info</c> does <em>not</em> see inlined data. A table holding
///                 only inlined rows reports zero files and zero bytes — indistinguishable from an
///                 empty table on the file figures alone. This is why <c>InlinedRows</c> exists.
///             </description>
///         </item>
///         <item>
///             <description>
///                 <c>ducklake_table_stats.record_count</c> is not a live row count and cannot be
///                 corrected into one from the metadata alone. It ignores merge-on-read deletes, and
///                 it also counts superseded <em>inlined</em> rows, whose tombstones are not delete
///                 files. Live rows therefore come from <c>count(*)</c>; see
///                 <see cref="CountRowsAsync"/> for the measurement behind that choice.
///             </description>
///         </item>
///         <item>
///             <description>
///                 Reads are read-only and need no writable attachment, so this works against a
///                 read-only share (invariant 9) for the same reason eject does (invariant 15).
///             </description>
///         </item>
///     </list>
/// </remarks>
public static class StorageBrowser
{
    /// <summary>Reads the storage footprint of the session's catalog.</summary>
    public static Task<CatalogStorageInfo> ReadAsync(Duckling duckling, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(duckling);

        // One gate acquisition for the alias probe and both reads, so the rollup and the target size
        // describe a single consistent state rather than two states with a write between them.
        return duckling.InvokeAsync(ct => ReadUnguardedAsync(duckling, ct), cancellationToken);
    }

    /// <summary>Reads the footprint with the session gate already held.</summary>
    /// <remarks>
    ///     Uses the unguarded execute path throughout, exactly as <see cref="MetadataExporter"/> does
    ///     and for the same reason: the gate is a non-reentrant semaphore, so re-entering it deadlocks.
    /// </remarks>
    internal static Task<CatalogStorageInfo> ReadUnguardedAsync(
        Duckling duckling,
        CancellationToken cancellationToken)
        => ReadUnguardedAsync(duckling, schemaName: null, tableName: null, cancellationToken);

    /// <summary>Reads one table's footprint with the session gate already held.</summary>
    internal static Task<CatalogStorageInfo> ReadTableUnguardedAsync(
        Duckling duckling,
        string schemaName,
        string tableName,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaName);
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        return ReadUnguardedAsync(duckling, schemaName, tableName, cancellationToken);
    }

    private static async Task<CatalogStorageInfo> ReadUnguardedAsync(
        Duckling duckling,
        string? schemaName,
        string? tableName,
        CancellationToken cancellationToken)
    {
        var metadata = await MetadataExporter
            .ResolveMetadataAliasAsync(duckling, cancellationToken)
            .ConfigureAwait(false);

        var catalog = duckling.Catalog.CatalogName;
        var meta = SqlIdentifier.Quote(metadata, nameof(metadata));
        var requestedTable = schemaName is null
            ? string.Empty
            : $"""

            WHERE s.schema_name = {SqlIdentifier.Literal(schemaName)}
              AND ti.table_name = {SqlIdentifier.Literal(tableName!)}
            """;

        // Rows that are live *in Parquet*: what the data files hold, less what the delete files
        // remove. Both figures describe filed data only and are correct for it. The derived counts
        // are summed, which widens them to HUGEINT — cast back rather than materialising a
        // BigInteger the wire projection would only have to narrow again.
        var sql = $"""
            SELECT
                s.schema_name,
                ti.table_name,
                CAST(COALESCE(df.filed_rows, 0) - COALESCE(dl.deleted_rows, 0) AS BIGINT) AS filed_rows,
                ti.file_count,
                ti.file_size_bytes,
                ti.delete_file_count,
                ti.delete_file_size_bytes
            FROM ducklake_table_info({SqlIdentifier.Literal(catalog)}) AS ti
            JOIN {meta}.ducklake_schema AS s
                ON s.schema_id = ti.schema_id AND s.end_snapshot IS NULL
            LEFT JOIN (
                SELECT table_id, sum(record_count) AS filed_rows
                FROM {meta}.ducklake_data_file
                WHERE end_snapshot IS NULL
                GROUP BY table_id
            ) AS df ON df.table_id = ti.table_id
            LEFT JOIN (
                SELECT table_id, sum(delete_count) AS deleted_rows
                FROM {meta}.ducklake_delete_file
                WHERE end_snapshot IS NULL
                GROUP BY table_id
            ) AS dl ON dl.table_id = ti.table_id
            {requestedTable}
            ORDER BY s.schema_name, ti.table_name
            """;

        var result = await duckling.ExecuteUnguardedAsync(sql, cancellationToken).ConfigureAwait(false);

        var rollup = new List<(string Schema, string Table, long FiledRows, long[] Files)>(result.Rows.Count);
        foreach (var row in result.Rows)
        {
            rollup.Add((
                Text(row[0]),
                Text(row[1]),
                Number(row[2]),
                [Number(row[3]), Number(row[4]), Number(row[5]), Number(row[6])]));
        }

        var live = await CountRowsAsync(duckling, rollup.Select(r => (r.Schema, r.Table)), cancellationToken)
            .ConfigureAwait(false);

        var tables = new List<TableStorageInfo>(rollup.Count);
        foreach (var (schema, table, filedRows, files) in rollup)
        {
            var rowCount = live.GetValueOrDefault((schema, table));
            tables.Add(new TableStorageInfo(
                schema,
                table,
                rowCount,
                // Whatever the table holds that Parquet does not. A negative reading would mean the
                // data files claim more live rows than the table has; clamp rather than surface it,
                // since the advisory it feeds is "flush has work to do" and a negative count is
                // noise, not a diagnosis.
                Math.Max(0, rowCount - filedRows),
                files[0],
                files[1],
                files[2],
                files[3]));
        }

        var target = await ReadTargetFileSizeAsync(duckling, meta, cancellationToken).ConfigureAwait(false);
        return new CatalogStorageInfo(tables, target);
    }

    /// <summary>Live row counts for every named table, in one round trip.</summary>
    /// <remarks>
    ///     <para>
    ///         <c>count(*)</c> rather than <c>ducklake_table_stats.record_count</c>, because that
    ///         column is wrong for this purpose in two different ways and only one of them is fixable
    ///         from the metadata. It ignores merge-on-read deletes, which live delete files do account
    ///         for — but it also counts <em>superseded inlined rows</em>, and those are not recorded
    ///         as delete files at all. Verified on 1.5.5: three inserts, one delete, and one update
    ///         against an unflushed table leave <c>record_count = 4</c> with <em>zero</em>
    ///         <c>ducklake_delete_file</c> rows, where the table plainly holds two. The tombstones are
    ///         in the per-table <c>ducklake_inlined_data_*</c> staging table, whose name is assigned at
    ///         run time (invariant 12) — so reconstructing the count from metadata means depending on
    ///         that naming, for a number the engine will compute exactly on request.
    ///     </para>
    ///     <para>
    ///         The cost objection does not survive measurement: <c>count(*)</c> over two million rows
    ///         is single-digit milliseconds, because DuckDB answers it from Parquet row-group metadata
    ///         rather than by scanning. One <c>UNION ALL</c> keeps it to a single round trip, so this
    ///         still does not scale round trips with catalog size — the property
    ///         <see cref="CatalogBrowser"/> is careful about.
    ///     </para>
    /// </remarks>
    private static async Task<Dictionary<(string Schema, string Table), long>> CountRowsAsync(
        Duckling duckling,
        IEnumerable<(string Schema, string Table)> tables,
        CancellationToken cancellationToken)
    {
        var counts = new Dictionary<(string, string), long>();

        // Escaped, not validated. These names came out of the catalog and are going back into a
        // statement, so the allow-list is the wrong tool: a tenant may create `order-items`,
        // `my.table`, or `select`, DuckLake stores all three, and refusing to name one would fail
        // the whole rollup rather than the one row — taking the Storage panel down for a catalog
        // the engine is perfectly happy with.
        var terms = tables
            .Select(t =>
                $"SELECT {SqlIdentifier.Literal(t.Schema)} AS s, {SqlIdentifier.Literal(t.Table)} AS t, "
                + $"count(*) AS c FROM {SqlIdentifier.QuoteName(t.Schema)}.{SqlIdentifier.QuoteName(t.Table)}")
            .ToArray();

        if (terms.Length == 0)
        {
            return counts;
        }

        var result = await duckling
            .ExecuteUnguardedAsync(string.Join("\nUNION ALL\n", terms), cancellationToken)
            .ConfigureAwait(false);

        foreach (var row in result.Rows)
        {
            counts[(Text(row[0]), Text(row[1]))] = Number(row[2]);
        }

        return counts;
    }

    /// <summary>
    ///     Lists one table's data files, optionally as they stood at <paramref name="snapshotId"/>.
    /// </summary>
    /// <param name="duckling">The session whose catalog is read.</param>
    /// <param name="schema">Schema the table belongs to.</param>
    /// <param name="table">Table name, unqualified.</param>
    /// <param name="snapshotId">
    ///     Snapshot to read the file list at, or null for the current one. A snapshot that predates
    ///     the table's creation raises rather than returning nothing — the same trap verified
    ///     behaviour 7 documents for the change feed — so the caller must be ready to report it.
    /// </param>
    /// <param name="maxRows">Ceiling on files returned; a materialising path, so invariant 6 applies.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    ///     <para>
    ///         <c>ducklake_list_files</c> also returns <c>data_file_encryption_key</c> and
    ///         <c>delete_file_encryption_key</c>, populated whenever the catalog is encrypted. Those
    ///         are secrets, and invariant 8's rule reaches them even though they arrive as columns
    ///         rather than as a connection string: the projection below names its columns explicitly
    ///         so a key can never reach a DTO, a response, or a log. Never <c>SELECT *</c> here.
    ///     </para>
    ///     <para>
    ///         The schema argument is always passed. Verified on 1.5.5: omitting it raises
    ///         "Table with name t2 does not exist" for anything outside the search path rather than
    ///         falling back to a search, so relying on the default would break every non-<c>main</c>
    ///         schema. The snapshot is interpolated as a literal because a table function cannot
    ///         contain a subquery — it is a <see cref="long"/>, so there is nothing to quote.
    ///     </para>
    /// </remarks>
    public static Task<TableFileList> ListFilesAsync(
        Duckling duckling,
        string schema,
        string table,
        long? snapshotId,
        int maxRows,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(duckling);
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxRows);

        return duckling.InvokeAsync(
            async ct =>
            {
                var snapshot = snapshotId is { } id
                    ? $", snapshot_version => {id.ToString(CultureInfo.InvariantCulture)}"
                    : string.Empty;

                // One more than asked for, so truncation is detected without a second count.
                var sql = $"""
                    SELECT
                        data_file,
                        data_file_size_bytes,
                        delete_file,
                        delete_file_size_bytes
                    FROM ducklake_list_files(
                        {SqlIdentifier.Literal(duckling.Catalog.CatalogName)},
                        {SqlIdentifier.Literal(table)},
                        schema => {SqlIdentifier.Literal(schema)}{snapshot})
                    ORDER BY data_file_size_bytes DESC, data_file
                    LIMIT {(maxRows + 1).ToString(CultureInfo.InvariantCulture)}
                    """;

                var result = await duckling.ExecuteUnguardedAsync(sql, ct).ConfigureAwait(false);

                var truncated = result.Rows.Count > maxRows;
                var files = new List<DataFileInfo>(Math.Min(result.Rows.Count, maxRows));
                foreach (var row in result.Rows.Take(maxRows))
                {
                    var deleteFile = Convert.ToString(row[2], CultureInfo.InvariantCulture);
                    files.Add(new DataFileInfo(
                        Text(row[0]),
                        Number(row[1]),
                        string.IsNullOrEmpty(deleteFile) ? null : deleteFile,
                        row[3] is null ? null : Number(row[3])));
                }

                return new TableFileList(schema, table, snapshotId, truncated, files);
            },
            cancellationToken);
    }

    /// <summary>
    ///     Reads the catalog's configured <c>target_file_size</c>, or null when it was never set.
    /// </summary>
    /// <remarks>
    ///     Set through <c>ducklake_set_option</c> and persisted to <c>ducklake_metadata</c> in bytes,
    ///     verified on 1.5.5: <c>'5MB'</c> is stored as <c>5000000</c>. The DuckDB setting
    ///     <c>ducklake_target_file_size</c> reads NULL by default and DuckLake's built-in default is
    ///     not exposed anywhere, so an unset option is reported as unset.
    /// </remarks>
    private static async Task<long?> ReadTargetFileSizeAsync(
        Duckling duckling,
        string quotedMetadataAlias,
        CancellationToken cancellationToken)
    {
        var result = await duckling
            .ExecuteUnguardedAsync(
                $"""
                SELECT value
                FROM {quotedMetadataAlias}.ducklake_metadata
                WHERE key = 'target_file_size' AND scope IS NULL
                """,
                cancellationToken)
            .ConfigureAwait(false);

        if (result.Rows.Count == 0)
        {
            return null;
        }

        var text = Convert.ToString(result.Rows[0][0], CultureInfo.InvariantCulture);
        return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bytes)
            ? bytes
            : null;
    }

    private static string Text(object? value) =>
        Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;

    private static long Number(object? value) =>
        value is null ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
}

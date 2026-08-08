using System.Globalization;
using DuckDB.EFCoreProvider.Extensions;
using Lakehold.Engine.Configuration;
using Lakehold.Engine.Execution;

namespace Lakehold.Engine.Catalog;

/// <summary>A committed snapshot of a catalog.</summary>
public sealed record SnapshotInfo(
    long SnapshotId,
    DateTimeOffset CommittedAt,
    long SchemaVersion,
    string? CommitMessage);

/// <summary>Outcome of a maintenance operation.</summary>
public sealed record MaintenanceResult(string Operation, string Detail, TimeSpan Elapsed, bool DryRun);

/// <summary>
///     Table-maintenance operations over a DuckLake catalog.
/// </summary>
/// <remarks>
///     <para>
///         MotherDuck performs these automatically and does not expose the controls. Lakehold
///         exposes them deliberately: a self-hosted operator owns the storage bill and the
///         compaction schedule, so hiding the knobs would remove a reason to self-host.
///     </para>
///     <para>
///         Mutating operations delegate to the provider's typed DuckLake facade. Snapshot reads use
///         DuckLake's table function directly so identifier/time keysets and the row limit execute
///         in DuckDB rather than materialising the full commit history in the control plane. Before
///         1.13.0 the mutations were hand-built <c>CALL</c> statements with interpolated timestamp
///         literals — precisely the string-building an ORM exists to eliminate.
///     </para>
///     <para>
///         <see cref="FlushInlinedDataAsync"/> deserves particular attention. DuckLake writes small
///         commits into the metadata catalog rather than to Parquet — verified on DuckDB 1.5.3,
///         where a two-row insert produced no data files and 200k rows produced one. Until inlined
///         data is flushed, "your data is open Parquet you can read without us" is only partly true,
///         because the newest rows live in the catalog database.
///     </para>
/// </remarks>
public static class LakehouseMaintenance
{
    /// <summary>Lists the catalog's snapshots, newest first.</summary>
    public static async Task<IReadOnlyList<SnapshotInfo>> ListSnapshotsAsync(
        Duckling duckling,
        int limit,
        CancellationToken cancellationToken)
        => await ListSnapshotsAsync(
                duckling,
                limit,
                upperSnapshotInclusive: null,
                beforeSnapshotExclusive: null,
                committedFromInclusive: null,
                committedToInclusive: null,
                cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    ///     Lists a stable keyset window of snapshots, newest first. Snapshot identifiers are the
    ///     source's monotonic commit key and therefore remain valid while newer commits arrive.
    /// </summary>
    public static async Task<IReadOnlyList<SnapshotInfo>> ListSnapshotsAsync(
        Duckling duckling,
        int limit,
        long? upperSnapshotInclusive,
        long? beforeSnapshotExclusive,
        DateTimeOffset? committedFromInclusive,
        DateTimeOffset? committedToInclusive,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(duckling);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        var predicates = new List<string>(4);
        if (upperSnapshotInclusive is { } upper)
        {
            predicates.Add($"snapshot_id <= {upper.ToString(CultureInfo.InvariantCulture)}");
        }

        if (beforeSnapshotExclusive is { } before)
        {
            predicates.Add($"snapshot_id < {before.ToString(CultureInfo.InvariantCulture)}");
        }

        if (committedFromInclusive is { } committedFrom)
        {
            predicates.Add($"snapshot_time >= {TimestampLiteral(committedFrom)}");
        }

        if (committedToInclusive is { } committedTo)
        {
            predicates.Add($"snapshot_time <= {TimestampLiteral(committedTo)}");
        }

        var where = predicates.Count == 0 ? string.Empty : $" WHERE {string.Join(" AND ", predicates)}";
        var sql =
            "SELECT snapshot_id, snapshot_time, schema_version, commit_message "
            + $"FROM ducklake_snapshots({SqlIdentifier.Literal(duckling.Catalog.CatalogName)})"
            + where
            + " ORDER BY snapshot_id DESC"
            + $" LIMIT {limit.ToString(CultureInfo.InvariantCulture)}";
        var result = await duckling.ExecuteQueryAsync(sql, cancellationToken).ConfigureAwait(false);

        return [.. result.Rows.Select(ToSnapshotInfo)];
    }

    /// <summary>Returns one retained snapshot by id, or null when it is not available.</summary>
    public static async Task<SnapshotInfo?> GetSnapshotAsync(
        Duckling duckling,
        long snapshotId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(duckling);
        ArgumentOutOfRangeException.ThrowIfNegative(snapshotId);

        var snapshots = await ListSnapshotsAsync(
                duckling,
                limit: 1,
                upperSnapshotInclusive: snapshotId,
                beforeSnapshotExclusive: null,
                committedFromInclusive: null,
                committedToInclusive: null,
                cancellationToken)
            .ConfigureAwait(false);

        return snapshots.FirstOrDefault(item => item.SnapshotId == snapshotId);
    }

    private static SnapshotInfo ToSnapshotInfo(object?[] row)
        => new(
            Convert.ToInt64(row[0], CultureInfo.InvariantCulture),
            ToTimestamp(row[1]),
            Convert.ToInt64(row[2], CultureInfo.InvariantCulture),
            row[3] is null or DBNull ? null : Convert.ToString(row[3], CultureInfo.InvariantCulture));

    private static DateTimeOffset ToTimestamp(object? value) => value switch
    {
        DateTimeOffset timestamp => timestamp,
        DateTime timestamp => new DateTimeOffset(
            DateTime.SpecifyKind(
                timestamp,
                timestamp.Kind == DateTimeKind.Unspecified ? DateTimeKind.Utc : timestamp.Kind)),
        _ => DateTimeOffset.Parse(
            Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal),
    };

    private static string TimestampLiteral(DateTimeOffset value)
        => $"{SqlIdentifier.Literal(value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture))}::TIMESTAMPTZ";

    /// <summary>
    ///     Exports the catalog's metadata to Parquet beside its data, so the storage location alone
    ///     is enough to reconstitute the lakehouse.
    /// </summary>
    public static Task<MaintenanceResult> BackupCatalogAsync(
        Duckling duckling,
        LakehouseOptions options,
        CancellationToken cancellationToken)
        => BackupCatalogAsync(duckling, options, expectedCurrentSnapshotId: null, cancellationToken);

    /// <summary>Exports metadata only if the reviewed catalog snapshot is still current.</summary>
    public static Task<MaintenanceResult> BackupCatalogAsync(
        Duckling duckling,
        LakehouseOptions options,
        long? expectedCurrentSnapshotId,
        CancellationToken cancellationToken)
        => RunAsync(
            duckling,
            "backup",
            dryRun: false,
            async (d, ct) =>
            {
                var r = await CatalogBackup.WriteAsync(d, options, TimeProvider.System, ct).ConfigureAwait(false);
                var size = r.Bytes is { } b ? $", {b:N0} bytes" : string.Empty;
                var pruned = r.PrunedGenerations > 0 ? $", pruned {r.PrunedGenerations} old generation(s)" : string.Empty;
                var retention = r.RetentionDeferred
                    ? ", retention deferred (object stores need a storage lifecycle rule)"
                    : string.Empty;
                return $"exported {r.TableCount} metadata table(s){size} to {r.Location}{pruned}{retention}";
            },
            expectedCurrentSnapshotId,
            commitMessage: null,
            cancellationToken);

    /// <summary>Writes inlined commits out as Parquet data files.</summary>
    public static Task<MaintenanceResult> FlushInlinedDataAsync(
        Duckling duckling,
        CancellationToken cancellationToken)
        => FlushInlinedDataAsync(duckling, expectedCurrentSnapshotId: null, cancellationToken);

    /// <summary>Writes inlined rows only if the reviewed catalog snapshot is still current.</summary>
    public static Task<MaintenanceResult> FlushInlinedDataAsync(
        Duckling duckling,
        long? expectedCurrentSnapshotId,
        CancellationToken cancellationToken)
        => RunAsync(
            duckling,
            "flush",
            dryRun: false,
            async (d, ct) =>
            {
                var results = await d.Maintenance
                    .FlushInlinedDataAsync(new DuckLakeFlushOptions(), ct)
                    .ConfigureAwait(false);

                var rows = results.Aggregate(
                    System.Numerics.BigInteger.Zero,
                    (total, r) => total + r.RowsFlushed);

                return $"flushed {rows.ToString(CultureInfo.InvariantCulture)} inlined row(s) to Parquet";
            },
            expectedCurrentSnapshotId,
            commitMessage: "lakehold maintenance: flush inlined data",
            cancellationToken);

    /// <summary>
    ///     Merges adjacent small Parquet files into larger ones. Small-file proliferation is the
    ///     dominant cause of slow scans in an append-heavy lakehouse.
    /// </summary>
    public static Task<MaintenanceResult> CompactAsync(
        Duckling duckling,
        CancellationToken cancellationToken)
        => CompactAsync(duckling, expectedCurrentSnapshotId: null, cancellationToken);

    /// <summary>Compacts files only if the reviewed catalog snapshot is still current.</summary>
    public static Task<MaintenanceResult> CompactAsync(
        Duckling duckling,
        long? expectedCurrentSnapshotId,
        CancellationToken cancellationToken)
        => RunAsync(
            duckling,
            "compact",
            dryRun: false,
            async (d, ct) =>
            {
                var results = await d.Maintenance
                    .MergeAdjacentFilesAsync(new DuckLakeMergeOptions(), ct)
                    .ConfigureAwait(false);

                var processed = results.Sum(r => r.FilesProcessed);
                var created = results.Sum(r => r.FilesCreated);
                return $"merged {processed} file(s) into {created}";
            },
            expectedCurrentSnapshotId,
            commitMessage: "lakehold maintenance: compact adjacent files",
            cancellationToken);

    /// <summary>
    ///     Drops snapshots older than <paramref name="olderThan"/>, bounding time-travel history and
    ///     making the files they pinned eligible for cleanup.
    /// </summary>
    /// <param name="dryRun">
    ///     When true, reports what would be expired without expiring it. Defaults to true because
    ///     expiry destroys time-travel history that cannot be recovered.
    /// </param>
    public static Task<MaintenanceResult> ExpireSnapshotsAsync(
        Duckling duckling,
        DateTimeOffset olderThan,
        bool dryRun,
        CancellationToken cancellationToken)
        => ExpireSnapshotsAsync(
            duckling, olderThan, dryRun, expectedCurrentSnapshotId: null, cancellationToken);

    /// <summary>Expires snapshots only if the reviewed catalog snapshot is still current.</summary>
    public static Task<MaintenanceResult> ExpireSnapshotsAsync(
        Duckling duckling,
        DateTimeOffset olderThan,
        bool dryRun,
        long? expectedCurrentSnapshotId,
        CancellationToken cancellationToken)
        => RunAsync(
            duckling,
            "expire",
            dryRun,
            async (d, ct) =>
            {
                var expired = await d.Maintenance.ExpireSnapshotsAsync(olderThan, dryRun, ct).ConfigureAwait(false);
                var verb = dryRun ? "would expire" : "expired";
                return $"{verb} {expired.Count} snapshot(s) committed before {olderThan:u}";
            },
            expectedCurrentSnapshotId,
            commitMessage: null,
            cancellationToken);

    /// <summary>
    ///     Deletes data files no longer referenced by any live snapshot.
    /// </summary>
    /// <remarks>
    ///     Run <see cref="ExpireSnapshotsAsync"/> first: a file is only orphaned once every snapshot
    ///     referencing it is gone, so cleanup before expiry is a no-op, and expiry without cleanup
    ///     leaves the storage bill unchanged.
    /// </remarks>
    public static Task<MaintenanceResult> CleanupOldFilesAsync(
        Duckling duckling,
        DateTimeOffset olderThan,
        bool dryRun,
        CancellationToken cancellationToken)
        => CleanupOldFilesAsync(
            duckling, olderThan, dryRun, expectedCurrentSnapshotId: null, cancellationToken);

    /// <summary>Deletes old files only if the reviewed catalog snapshot is still current.</summary>
    public static Task<MaintenanceResult> CleanupOldFilesAsync(
        Duckling duckling,
        DateTimeOffset olderThan,
        bool dryRun,
        long? expectedCurrentSnapshotId,
        CancellationToken cancellationToken)
        => RunAsync(
            duckling,
            "cleanup",
            dryRun,
            async (d, ct) =>
            {
                var files = await d.Maintenance.CleanupOldFilesAsync(olderThan, dryRun, ct).ConfigureAwait(false);
                var verb = dryRun ? "would delete" : "deleted";
                return $"{verb} {files.Count} unreferenced file(s)";
            },
            expectedCurrentSnapshotId,
            commitMessage: null,
            cancellationToken);

    /// <param name="commitMessage">
    ///     Label for the snapshot the operation commits, or null for an operation that commits
    ///     nothing. Only flush and compaction write: backup exports, and expiry and cleanup remove
    ///     snapshots and files rather than adding one to label.
    /// </param>
    private static async Task<MaintenanceResult> RunAsync(
        Duckling duckling,
        string operation,
        bool dryRun,
        Func<Duckling, CancellationToken, Task<string>> action,
        long? expectedCurrentSnapshotId,
        string? commitMessage,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(duckling);

        var startedAt = TimeProvider.System.GetTimestamp();
        async Task<string> RunGuardedAsync(CancellationToken ct)
        {
            if (expectedCurrentSnapshotId is { } expected)
            {
                var current = await CurrentSnapshotUnguardedAsync(duckling, ct).ConfigureAwait(false);
                if (current != expected)
                {
                    throw new ArgumentException(
                        $"The catalog advanced from snapshot {expected.ToString(CultureInfo.InvariantCulture)} "
                        + $"to {current.ToString(CultureInfo.InvariantCulture)} after this maintenance was reviewed. "
                        + "Review a fresh plan; no maintenance was applied.");
                }
            }

            return await action(duckling, ct).ConfigureAwait(false);
        }

        var detail = commitMessage is null
            ? await duckling.InvokeAsync(RunGuardedAsync, cancellationToken).ConfigureAwait(false)
            : await duckling
                .InvokeLabelledAsync(commitMessage, RunGuardedAsync, cancellationToken)
                .ConfigureAwait(false);

        return new MaintenanceResult(operation, detail, TimeProvider.System.GetElapsedTime(startedAt), dryRun);
    }

    private static async Task<long> CurrentSnapshotUnguardedAsync(
        Duckling duckling,
        CancellationToken cancellationToken)
    {
        var result = await duckling
            .ExecuteUnguardedAsync(
                "SELECT coalesce(max(snapshot_id), 0) FROM ducklake_snapshots("
                + $"{SqlIdentifier.Literal(duckling.Catalog.CatalogName)})",
                cancellationToken)
            .ConfigureAwait(false);
        return Convert.ToInt64(result.Rows.Single()[0], CultureInfo.InvariantCulture);
    }
}

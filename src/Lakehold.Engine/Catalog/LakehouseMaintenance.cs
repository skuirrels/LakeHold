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
///         Every operation delegates to the provider's typed DuckLake facade. Before 1.13.0 these
///         were hand-built <c>CALL</c> statements with interpolated timestamp literals — precisely
///         the string-building an ORM exists to eliminate.
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
    {
        ArgumentNullException.ThrowIfNull(duckling);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        var snapshots = await duckling
            .InvokeAsync(ct => duckling.Maintenance.GetSnapshotsAsync(ct), cancellationToken)
            .ConfigureAwait(false);

        return [.. snapshots
            .OrderByDescending(s => s.SnapshotId)
            .Take(limit)
            .Select(s => new SnapshotInfo(s.SnapshotId, s.SnapshotTime, s.SchemaVersion, s.CommitMessage))];
    }

    /// <summary>
    ///     Exports the catalog's metadata to Parquet beside its data, so the storage location alone
    ///     is enough to reconstitute the lakehouse.
    /// </summary>
    public static Task<MaintenanceResult> BackupCatalogAsync(
        Duckling duckling,
        LakehouseOptions options,
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
            cancellationToken);

    /// <summary>Writes inlined commits out as Parquet data files.</summary>
    public static Task<MaintenanceResult> FlushInlinedDataAsync(Duckling duckling, CancellationToken cancellationToken)
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
            cancellationToken);

    /// <summary>
    ///     Merges adjacent small Parquet files into larger ones. Small-file proliferation is the
    ///     dominant cause of slow scans in an append-heavy lakehouse.
    /// </summary>
    public static Task<MaintenanceResult> CompactAsync(Duckling duckling, CancellationToken cancellationToken)
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
            cancellationToken);

    private static async Task<MaintenanceResult> RunAsync(
        Duckling duckling,
        string operation,
        bool dryRun,
        Func<Duckling, CancellationToken, Task<string>> action,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(duckling);

        var startedAt = TimeProvider.System.GetTimestamp();
        var detail = await duckling
            .InvokeAsync(ct => action(duckling, ct), cancellationToken)
            .ConfigureAwait(false);

        return new MaintenanceResult(operation, detail, TimeProvider.System.GetElapsedTime(startedAt), dryRun);
    }
}

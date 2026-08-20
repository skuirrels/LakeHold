using System.ComponentModel;
using Lakehold.ControlPlane.Data;
using ModelContextProtocol.Server;

namespace Lakehold.Api.Mcp;

/// <summary>
///     Table-data restore from a retained snapshot, as the same two-step contract the HTTP route
///     offers.
/// </summary>
/// <remarks>
///     <para>
///         Time travel is the capability this surface leads with, and until now an agent could read
///         what a table used to contain and had no supported way to put it back — leaving
///         "reconstruct it with `execute`" as the only route, which is precisely the hand-written
///         <c>CREATE OR REPLACE TABLE … AT (…)</c> that invariant 22 exists to forbid. Projecting the
///         real endpoint gives the agent the safe path instead of an incentive to improvise a
///         dangerous one.
///     </para>
///     <para>
///         The engine keeps the guarantees: the current table definition survives, historical rows
///         are staged and inserted through the existing table inside one labelled transaction, and an
///         incompatibility rolls the whole thing back. This type adds nothing to that and must not —
///         it is a projection, not a second implementation.
///     </para>
///     <para>
///         Capability is <c>TenantWrite</c>, matching the HTTP route rather than the maintenance
///         tools' <c>TenantOwner</c>. A restore rewrites one table's rows, which is the same authority
///         an <c>UPDATE</c> needs; it does not destroy history or touch storage the way expiry and
///         cleanup do, so it sits behind Allow writes with the other mutations rather than behind the
///         separate operator tier.
///     </para>
/// </remarks>
[McpServerToolType]
public sealed class LakeholdRestoreTools(
    LakehouseService lakehouse,
    IHttpContextAccessor httpContextAccessor)
{
    /// <summary>Reports what a restore would change, without changing anything.</summary>
    [McpServerTool(
        Name = "plan_table_restore",
        Title = "Plan a table restore from a snapshot",
        ReadOnly = true,
        Destructive = false,
        UseStructuredContent = true,
        OpenWorld = false)]
    [Description(
        "Reports what restoring one table's rows from a retained snapshot would change: current and "
        + "historical row counts, and which columns are shared, current-only, or historical-only. "
        + "Changes nothing. The returned currentSnapshotId must be passed to apply_table_restore.")]
    public Task<McpTableRestorePlan> PlanAsync(
        [Description("Tenant slug the catalog belongs to.")] string tenant,
        [Description("Catalog holding the table.")] string catalog,
        [Description("Table whose rows would be restored. Its current definition is preserved.")] string table,
        [Description("Snapshot to restore the rows from. Ids come from list_snapshots.")] long snapshotId,
        CancellationToken cancellationToken,
        [Description("Schema holding the table. Defaults to 'main'.")] string schema = "main")
    {
        McpCaller.AuthorizeWriter(httpContextAccessor, tenant, catalog);

        return McpFailure.GuardAsync(async () =>
        {
            var result = await lakehouse
                .RestoreTableAsync(
                    tenant, catalog, schema, table, snapshotId,
                    apply: false, expectedCurrentSnapshotId: null, cancellationToken)
                .ConfigureAwait(false);

            return new McpTableRestorePlan(
                result.Schema,
                result.Table,
                result.SnapshotId,
                result.CurrentSnapshotId,
                result.CurrentRowCount,
                result.HistoricalRowCount,
                result.RestoredColumns,
                result.CurrentOnlyColumns,
                result.HistoricalOnlyColumns);
        });
    }

    /// <summary>Applies a reviewed restore, provided the catalog has not moved since.</summary>
    /// <remarks>
    ///     <paramref name="currentSnapshotId"/> is the fence, and it is required rather than optional.
    ///     A restore decided against one state of the table and applied against another is a silent
    ///     data loss, so an intervening commit must force the agent to plan again rather than proceed
    ///     on a stale reading.
    /// </remarks>
    [McpServerTool(
        Name = "apply_table_restore",
        Title = "Apply a reviewed table restore",
        ReadOnly = false,
        Destructive = true,
        Idempotent = false,
        UseStructuredContent = true,
        OpenWorld = false)]
    [Description(
        "Replaces one table's rows with those from a retained snapshot, preserving the table's current "
        + "definition, in a single transaction that rolls back on any incompatibility. Applies only "
        + "while the catalog is still at the snapshot plan_table_restore reported; an intervening "
        + "commit forces a fresh plan. Show the plan to the user before calling this.")]
    public Task<McpTableRestoreResult> ApplyAsync(
        [Description("Tenant slug the catalog belongs to.")] string tenant,
        [Description("Catalog holding the table.")] string catalog,
        [Description("Table to restore. Its current definition is preserved.")] string table,
        [Description("Snapshot to restore the rows from, as reviewed in the plan.")] long snapshotId,
        [Description("The currentSnapshotId returned by plan_table_restore. Fences an intervening commit.")]
        long currentSnapshotId,
        CancellationToken cancellationToken,
        [Description("Schema holding the table. Defaults to 'main'.")] string schema = "main")
    {
        McpCaller.AuthorizeForWrite(httpContextAccessor, tenant, catalog);

        return McpFailure.GuardAsync(async () =>
        {
            var result = await lakehouse
                .RestoreTableAsync(
                    tenant, catalog, schema, table, snapshotId,
                    apply: true, currentSnapshotId, cancellationToken)
                .ConfigureAwait(false);

            return new McpTableRestoreResult(
                result.Schema,
                result.Table,
                result.SnapshotId,
                result.CurrentSnapshotId,
                result.HistoricalRowCount,
                result.RestoredColumns);
        });
    }
}

/// <summary>What a restore would do, before anything is changed.</summary>
/// <param name="CurrentSnapshotId">
///     The catalog's head at planning time, and the fence <c>apply_table_restore</c> requires.
/// </param>
/// <param name="CurrentRowCount">Rows the table holds now, and would no longer hold after applying.</param>
/// <param name="HistoricalRowCount">Rows the snapshot holds, and the table would hold after applying.</param>
/// <param name="RestoredColumns">Columns present in both, which carry their historical values across.</param>
/// <param name="CurrentOnlyColumns">
///     Columns the table has now and the snapshot does not. They survive the restore — the table
///     definition is preserved — and take their declared default or null for every restored row.
/// </param>
/// <param name="HistoricalOnlyColumns">
///     Columns the snapshot has and the table no longer does. Their values are <em>not</em> restored,
///     because restoring them would mean changing the table's current definition.
/// </param>
public sealed record McpTableRestorePlan(
    string Schema,
    string Table,
    long SnapshotId,
    long CurrentSnapshotId,
    long CurrentRowCount,
    long HistoricalRowCount,
    IReadOnlyList<string> RestoredColumns,
    IReadOnlyList<string> CurrentOnlyColumns,
    IReadOnlyList<string> HistoricalOnlyColumns);

/// <summary>The outcome of a committed restore.</summary>
/// <param name="CurrentSnapshotId">The new head the restore committed, not the one it was fenced on.</param>
/// <param name="RestoredRowCount">Rows written into the table from the snapshot.</param>
public sealed record McpTableRestoreResult(
    string Schema,
    string Table,
    long SnapshotId,
    long CurrentSnapshotId,
    long RestoredRowCount,
    IReadOnlyList<string> RestoredColumns);

using System.ComponentModel;
using Lakehold.ControlPlane.Data;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Lakehold.Api.Mcp;

/// <summary>Snapshot-bound maintenance commands behind the explicit operator tier.</summary>
[McpServerToolType]
public sealed class LakeholdMaintenanceTools(
    LakehouseService lakehouse,
    IHttpContextAccessor httpContextAccessor)
{
    private static readonly string[] SupportedOperations = ["flush", "compact", "expire", "cleanup"];

    [McpServerTool(Name = "plan_maintenance", Title = "Plan catalog maintenance", ReadOnly = true, Destructive = false, UseStructuredContent = true, OpenWorld = false)]
    [McpMeta(McpExtensions.OperatorCommandMetadata, true)]
    [Description(
        "Plans flush, compact, snapshot expiry, or old-file cleanup without changing the catalog. "
        + "The returned currentSnapshotId must be supplied to apply_maintenance.")]
    public Task<McpMaintenancePlan> PlanAsync(
        [Description("Tenant slug the catalog belongs to.")] string tenant,
        [Description("Catalog to plan maintenance for.")] string catalog,
        [Description(
            "One of: 'flush' (write inlined rows to Parquet), 'compact' (rewrite small files and "
            + "deletes), 'expire' (drop snapshots past the deployment's retention — DESTROYS time "
            + "travel), 'cleanup' (delete unreferenced data files — IRREVERSIBLE).")]
        string operation,
        CancellationToken cancellationToken)
    {
        McpCaller.AuthorizeOperator(httpContextAccessor, tenant, catalog, requireWrites: false);
        EnsureOperation(operation);

        // Stateful: the expire dry run reports a CDC retention blocker as InvalidOperationException,
        // which is a fact about the catalog the caller needs rather than an internal failure.
        return McpFailure.GuardStatefulAsync(async () =>
        {
            var snapshot = await CurrentSnapshotAsync(tenant, catalog, cancellationToken).ConfigureAwait(false);
            if (operation is "expire" or "cleanup")
            {
                var dryRun = await lakehouse
                    .RunMaintenanceAsync(tenant, catalog, operation, apply: false, cancellationToken)
                    .ConfigureAwait(false);
                return new McpMaintenancePlan(operation, snapshot, dryRun.Detail);
            }

            var detail = operation == "flush"
                ? "Flush currently inlined rows into Parquet files."
                : "Rewrite table data files to reduce small-file and delete overhead.";
            return new McpMaintenancePlan(operation, snapshot, detail);
        });
    }

    [McpServerTool(Name = "apply_maintenance", Title = "Apply planned catalog maintenance", ReadOnly = false, Destructive = true, UseStructuredContent = true, OpenWorld = false)]
    [McpMeta(McpExtensions.OperatorCommandMetadata, true)]
    [Description(
        "Applies a previously reviewed maintenance plan only when the catalog is still at that "
        + "plan's current snapshot. An intervening commit forces a fresh plan. 'expire' and 'cleanup' "
        + "are irreversible — show the plan to the user before calling this.")]
    public Task<McpMaintenanceResult> ApplyAsync(
        [Description("Tenant slug the catalog belongs to.")] string tenant,
        [Description("Catalog to maintain.")] string catalog,
        [Description("The same operation that was planned: 'flush', 'compact', 'expire', or 'cleanup'.")]
        string operation,
        [Description("The currentSnapshotId returned by plan_maintenance. Fences an intervening commit.")]
        long currentSnapshotId,
        CancellationToken cancellationToken)
    {
        McpCaller.AuthorizeOperator(httpContextAccessor, tenant, catalog, requireWrites: true);
        EnsureOperation(operation);

        return McpFailure.GuardStatefulAsync(async () =>
        {
            var result = await lakehouse
                .RunMaintenanceAsync(
                    tenant,
                    catalog,
                    operation,
                    apply: true,
                    expectedCurrentSnapshotId: currentSnapshotId,
                    cancellationToken)
                .ConfigureAwait(false);
            return new McpMaintenanceResult(
                result.Operation,
                result.Detail,
                result.Elapsed.TotalMilliseconds,
                currentSnapshotId);
        });
    }

    private async Task<long> CurrentSnapshotAsync(
        string tenant,
        string catalog,
        CancellationToken cancellationToken)
    {
        var snapshots = await lakehouse
            .GetSnapshotsAsync(tenant, catalog, 1, cancellationToken)
            .ConfigureAwait(false);
        return snapshots.Count == 0 ? 0 : snapshots[0].SnapshotId;
    }

    private static void EnsureOperation(string operation)
    {
        if (!SupportedOperations.Contains(operation, StringComparer.Ordinal))
        {
            throw new McpException(
                $"Unknown maintenance operation '{operation}'. Expected flush, compact, expire, or cleanup.");
        }
    }
}

/// <summary>What a maintenance operation would do, and the snapshot it was reviewed against.</summary>
/// <param name="CurrentSnapshotId">
///     The catalog's head at planning time. <c>apply_maintenance</c> requires it and refuses if the
///     catalog has committed anything since.
/// </param>
public sealed record McpMaintenancePlan(string Operation, long CurrentSnapshotId, string Detail);

/// <summary>The outcome of an applied maintenance operation.</summary>
public sealed record McpMaintenanceResult(
    string Operation,
    string Detail,
    double ElapsedMilliseconds,
    long PlannedSnapshotId);

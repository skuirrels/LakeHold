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

    [McpServerTool(Name = "plan_maintenance", Title = "Plan catalog maintenance", ReadOnly = true, Destructive = false)]
    [McpMeta(McpExtensions.OperatorCommandMetadata, true)]
    [Description(
        "Plans flush, compact, snapshot expiry, or old-file cleanup without changing the catalog. "
        + "The returned currentSnapshotId must be supplied to apply_maintenance.")]
    public async Task<McpMaintenancePlan> PlanAsync(
        string tenant,
        string catalog,
        string operation,
        CancellationToken cancellationToken)
    {
        McpCaller.AuthorizeOperator(httpContextAccessor, tenant, catalog, requireWrites: false);
        EnsureOperation(operation);

        try
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
        }
        catch (Exception exception) when (exception is CatalogNotFoundException or InvalidOperationException)
        {
            throw new McpException(exception.Message);
        }
    }

    [McpServerTool(Name = "apply_maintenance", Title = "Apply planned catalog maintenance", ReadOnly = false, Destructive = true)]
    [McpMeta(McpExtensions.OperatorCommandMetadata, true)]
    [Description(
        "Applies a previously reviewed maintenance plan only when the catalog is still at that "
        + "plan's current snapshot. An intervening commit forces a fresh plan.")]
    public async Task<McpMaintenanceResult> ApplyAsync(
        string tenant,
        string catalog,
        string operation,
        long currentSnapshotId,
        CancellationToken cancellationToken)
    {
        McpCaller.AuthorizeOperator(httpContextAccessor, tenant, catalog, requireWrites: true);
        EnsureOperation(operation);

        try
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
        }
        catch (McpException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is CatalogNotFoundException or InvalidOperationException or ArgumentException)
        {
            throw new McpException(exception.Message);
        }
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

public sealed record McpMaintenancePlan(string Operation, long CurrentSnapshotId, string Detail);

public sealed record McpMaintenanceResult(
    string Operation,
    string Detail,
    double ElapsedMilliseconds,
    long PlannedSnapshotId);

using Lakehold.ControlPlane.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Lakehold.Api.Health;

/// <summary>
///     Readiness check: can this node reach its control plane?
/// </summary>
/// <remarks>
///     <para>
///         Readiness has to test something a starting node can fail. Without this, <c>/health</c>
///         would report ready the moment the process listens — before the control-plane database is
///         open and before seeding finishes — and an orchestrator would route traffic to a node whose
///         every query is about to fail. A probe that cannot fail is worse than no probe, because it
///         is trusted.
///     </para>
///     <para>
///         Deliberately only a connectivity test. A readiness probe runs every few seconds for the
///         life of the node, so it must not attach catalogs, start a compute session, or touch tenant
///         data; the control plane being open is the one dependency that makes a node able to serve
///         at all. This is a <em>readiness</em> check only and is untagged, so it never gates
///         <c>/alive</c> — a node that cannot reach its database needs traffic withheld, not a
///         restart loop that cannot fix it.
///     </para>
/// </remarks>
internal sealed class ControlPlaneHealthCheck(ControlPlaneContext context) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext healthCheckContext,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await context.Database.CanConnectAsync(cancellationToken).ConfigureAwait(false)
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("The control-plane database is not reachable.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The detail reaches logs and the dashboard, never the probe response: the endpoint is
            // unauthenticated and answers with a bare status.
            return HealthCheckResult.Unhealthy("The control-plane database could not be opened.", ex);
        }
    }
}

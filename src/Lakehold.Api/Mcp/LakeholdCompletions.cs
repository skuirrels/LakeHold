using Lakehold.ControlPlane.Data;
using Lakehold.ControlPlane.Model;
using Lakehold.ControlPlane.Security;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Lakehold.Api.Mcp;

/// <summary>
///     Argument auto-completion for the resource templates, so a client can offer real tenant,
///     catalog, and snapshot values instead of asking a person to type an opaque URI.
/// </summary>
/// <remarks>
///     <para>
///         The resources are templates rather than a concrete list, deliberately — enumerating every
///         reachable catalog during resource *listing* is the one place a mistake would hand names to
///         a caller that cannot reach them. Completion is the supported way back: it is a request the
///         client makes with the same credential, for one argument at a time, and it can therefore
///         answer from what that credential can reach without ever disclosing more.
///     </para>
///     <para>
///         So the security property is that completion discloses <b>nothing a tool would not</b>.
///         Tenants and catalogs come from the same principal-scoped query <c>list_tenants</c> uses,
///         including its stricter narrowing rule; snapshot ids go through
///         <see cref="McpCaller.Authorize"/> exactly as <c>list_snapshots</c> does, so an unreachable
///         catalog completes to nothing rather than to a refusal that would confirm it exists
///         (invariant 19). An unknown template or argument completes to nothing at all.
///     </para>
/// </remarks>
internal static class LakeholdCompletions
{
    /// <summary>The protocol's ceiling on suggestions in one response.</summary>
    private const int MaxValues = 100;

    /// <summary>
    ///     How many snapshots to search before applying the caller's prefix.
    /// </summary>
    /// <remarks>
    ///     Bounded rather than unbounded: completion is a convenience and must not turn into a full
    ///     history scan on a catalog with a long retention. Deep enough that a prefix matches
    ///     something on any realistic catalog, shallow enough to stay a cheap read.
    /// </remarks>
    private const int SnapshotSearchDepth = 1_000;

    private const string SchemaTemplate = "lakehold://{tenant}/{catalog}/schema";
    private const string SnapshotTemplate = "lakehold://{tenant}/{catalog}/snapshots/{snapshotId}";

    /// <summary>Suggests values for one template argument, scoped to the calling credential.</summary>
    public static async ValueTask<CompleteResult> CompleteAsync(
        RequestContext<CompleteRequestParams> context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var services = context.Services
            ?? throw new McpException("No request services are available.");
        var parameters = context.Params
            ?? throw new McpException("A completion request must carry parameters.");

        if (parameters.Ref is not ResourceTemplateReference { Uri: { } uri }
            || uri is not (SchemaTemplate or SnapshotTemplate))
        {
            return Empty();
        }

        var accessor = services.GetRequiredService<IHttpContextAccessor>();
        var prefix = parameters.Argument.Value ?? string.Empty;
        var known = parameters.Context?.Arguments;

        var values = parameters.Argument.Name switch
        {
            "tenant" => await TenantsAsync(services, accessor, prefix, cancellationToken).ConfigureAwait(false),
            "catalog" => await CatalogsAsync(services, accessor, Argument(known, "tenant"), prefix, cancellationToken)
                .ConfigureAwait(false),
            "snapshotId" when uri == SnapshotTemplate => await SnapshotsAsync(
                    services, accessor, Argument(known, "tenant"), Argument(known, "catalog"), prefix, cancellationToken)
                .ConfigureAwait(false),
            _ => [],
        };

        return new CompleteResult
        {
            Completion = new Completion
            {
                Values = values,
                Total = values.Count,
                HasMore = values.Count >= MaxValues,
            },
        };
    }

    private static async Task<IList<string>> TenantsAsync(
        IServiceProvider services,
        IHttpContextAccessor accessor,
        string prefix,
        CancellationToken cancellationToken)
    {
        var principal = McpCaller.Principal(accessor);
        var control = services.GetRequiredService<ControlPlaneContext>();

        return await Scoped(control.Tenants.AsNoTracking(), principal)
            .Where(tenant => tenant.Slug.StartsWith(prefix))
            .OrderBy(tenant => tenant.Slug)
            .Select(tenant => tenant.Slug)
            .Take(MaxValues)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<IList<string>> CatalogsAsync(
        IServiceProvider services,
        IHttpContextAccessor accessor,
        string? tenant,
        string prefix,
        CancellationToken cancellationToken)
    {
        // Without a tenant there is nothing to scope to, and completing every catalog on the node
        // would be exactly the disclosure the template shape exists to avoid.
        if (string.IsNullOrEmpty(tenant))
        {
            return [];
        }

        var principal = McpCaller.Principal(accessor);
        var control = services.GetRequiredService<ControlPlaneContext>();
        var narrowedTo = principal.CatalogName;

        return await Scoped(control.Tenants.AsNoTracking(), principal)
            .Where(t => t.Slug == tenant)
            .SelectMany(t => t.Catalogs)
            .Where(catalog => (narrowedTo == null || catalog.Name == narrowedTo) && catalog.Name.StartsWith(prefix))
            .OrderBy(catalog => catalog.Name)
            .Select(catalog => catalog.Name)
            .Take(MaxValues)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<IList<string>> SnapshotsAsync(
        IServiceProvider services,
        IHttpContextAccessor accessor,
        string? tenant,
        string? catalog,
        string prefix,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(tenant) || string.IsNullOrEmpty(catalog))
        {
            return [];
        }

        // Same capability check a tool makes. A caller that cannot reach the catalog gets an empty
        // list, not an error: a refusal here would answer "does that catalog exist?" through a
        // side channel the tools are careful to close.
        try
        {
            McpCaller.Authorize(accessor, tenant, catalog);
        }
        catch (McpException)
        {
            return [];
        }

        var lakehouse = services.GetRequiredService<LakehouseService>();
        try
        {
            // Read a wider window than we return, because the prefix is applied *after* the fetch:
            // capping at MaxValues first would mean a caller typing "7" on a catalog with hundreds of
            // snapshots gets nothing whenever no id in the newest hundred happens to start with 7 —
            // a valid snapshot completing to silence, which reads as "no such snapshot".
            var snapshots = await lakehouse
                .GetSnapshotsAsync(tenant, catalog, SnapshotSearchDepth, cancellationToken)
                .ConfigureAwait(false);

            return
            [
                .. snapshots
                    .Select(snapshot => snapshot.SnapshotId.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .Where(id => id.StartsWith(prefix, StringComparison.Ordinal))
                    .Take(MaxValues),
            ];
        }
        catch (Exception exception) when (McpFailure.IsExpected(exception))
        {
            // A catalog that has never been written to cannot be attached read-only. Completion is a
            // convenience; failing it must not fail the client's session.
            return [];
        }
    }

    /// <summary>
    ///     The tenant query narrowed to the principal, matching <c>list_tenants</c> exactly.
    /// </summary>
    private static IQueryable<Tenant> Scoped(IQueryable<Tenant> tenants, ILakeholdPrincipal principal)
        => principal.Scope != TokenScope.Tenant
            ? tenants
            : principal.TenantSlug is { } slug
                ? tenants.Where(tenant => tenant.Slug == slug)
                : tenants.Where(_ => false);

    private static string? Argument(IDictionary<string, string>? arguments, string name)
        => arguments is not null && arguments.TryGetValue(name, out var value) ? value : null;

    private static CompleteResult Empty()
        => new() { Completion = new Completion { Values = [], Total = 0, HasMore = false } };
}

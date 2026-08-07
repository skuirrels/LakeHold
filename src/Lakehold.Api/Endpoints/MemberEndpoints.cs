using Lakehold.Api.Auth;
using Lakehold.ControlPlane.Data;
using Lakehold.ControlPlane.Model;
using Lakehold.ControlPlane.Security;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Lakehold.Api.Endpoints;

/// <summary>
///     Administering the users who may reach a workspace.
/// </summary>
/// <remarks>
///     These are what make membership something an operator owns rather than a property of whatever
///     the identity provider happens to assert. Every route requires <see cref="Capability.TenantAdmin"/>,
///     which an instance credential holds for any tenant and a full owner holds for its own — the
///     same rule that already governs issuing API tokens, because granting a person access and
///     issuing a machine credential are the same kind of decision.
/// </remarks>
public static class MemberEndpoints
{
    /// <summary>Maps membership administration under an already-authorized tenant group.</summary>
    public static IEndpointRouteBuilder MapMemberEndpoints(this IEndpointRouteBuilder tenants)
    {
        ArgumentNullException.ThrowIfNull(tenants);

        tenants.MapGet("/{tenantSlug}/members", ListAsync)
            .RequireCapability(Capability.TenantAdmin)
            .WithSummary("Lists the users who may reach this workspace, including those awaiting approval.");

        tenants.MapPatch("/{tenantSlug}/members/{id:int}", UpdateAsync)
            .RequireCapability(Capability.TenantAdmin)
            .WithSummary("Changes a member's role or status.");

        tenants.MapDelete("/{tenantSlug}/members/{id:int}", RemoveAsync)
            .RequireCapability(Capability.TenantAdmin)
            .WithSummary("Removes a membership entirely; the person may return as a new arrival.");

        return tenants;
    }

    internal static async Task<Results<Ok<IReadOnlyList<TenantMemberDto>>, NotFound<string>>> ListAsync(
        string tenantSlug,
        ControlPlaneContext context,
        CancellationToken cancellationToken)
    {
        var tenantId = await TenantIdAsync(context, tenantSlug, cancellationToken).ConfigureAwait(false);
        if (tenantId is null)
        {
            return TypedResults.NotFound($"Tenant '{tenantSlug}' was not found.");
        }

        var members = await context.TenantMembers
            .AsNoTracking()
            .Where(m => m.TenantId == tenantId)
            // Pending first: someone waiting on an administrator is the only row that needs acting
            // on, and burying it under established members is how a request goes unanswered.
            .OrderBy(m => m.Status == MemberStatus.Pending ? 0 : 1)
            .ThenBy(m => m.DisplayName ?? m.Subject)
            .Select(m => new TenantMemberDto(
                m.Id,
                m.Subject,
                m.DisplayName,
                m.Email,
                m.Role.ToString().ToLowerInvariant(),
                m.Status.ToString().ToLowerInvariant(),
                m.CreatedUtc,
                m.LastSeenUtc))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return TypedResults.Ok<IReadOnlyList<TenantMemberDto>>(members);
    }

    internal static async Task<Results<Ok<TenantMemberDto>, NotFound<string>, BadRequest<string>>> UpdateAsync(
        string tenantSlug,
        int id,
        UpdateTenantMemberRequest request,
        ControlPlaneContext context,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return TypedResults.BadRequest("A role or status is required.");
        }

        var member = await FindAsync(context, tenantSlug, id, cancellationToken).ConfigureAwait(false);
        if (member is null)
        {
            return TypedResults.NotFound($"Member {id} was not found in tenant '{tenantSlug}'.");
        }

        if (request.Role is { Length: > 0 } role)
        {
            if (!TryParseRole(role, out var parsed))
            {
                return TypedResults.BadRequest("Role must be owner, editor, or reader.");
            }

            member.Role = parsed;
        }

        if (request.Status is { Length: > 0 } status)
        {
            if (!Enum.TryParse<MemberStatus>(status, ignoreCase: true, out var parsed))
            {
                return TypedResults.BadRequest("Status must be pending, active, or suspended.");
            }

            member.Status = parsed;
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(ToDto(member));
    }

    internal static async Task<Results<NoContent, NotFound<string>>> RemoveAsync(
        string tenantSlug,
        int id,
        ControlPlaneContext context,
        CancellationToken cancellationToken)
    {
        var member = await FindAsync(context, tenantSlug, id, cancellationToken).ConfigureAwait(false);
        if (member is null)
        {
            return TypedResults.NotFound($"Member {id} was not found in tenant '{tenantSlug}'.");
        }

        // Removal, not suspension: the person can sign in again and arrive as a newcomer. Suspending
        // is the option that keeps them listed and refused; both exist because they answer different
        // questions, and collapsing them would lose the one that keeps a name against past activity.
        context.TenantMembers.Remove(member);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return TypedResults.NoContent();
    }

    /// <remarks>
    ///     Deliberately refuses an unrecognised role rather than falling back to a safe one. A typo
    ///     that silently produces a reader looks like the request worked and is discovered later, by
    ///     someone who cannot do their job.
    /// </remarks>
    private static bool TryParseRole(string value, out TokenRole role)
        => Enum.TryParse(value, ignoreCase: true, out role) && Enum.IsDefined(role);

    private static async Task<int?> TenantIdAsync(
        ControlPlaneContext context,
        string slug,
        CancellationToken cancellationToken)
    {
        var tenant = await context.Tenants
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Slug == slug, cancellationToken)
            .ConfigureAwait(false);
        return tenant?.Id;
    }

    private static async Task<TenantMember?> FindAsync(
        ControlPlaneContext context,
        string tenantSlug,
        int id,
        CancellationToken cancellationToken)
        => await context.TenantMembers
            .Include(m => m.Tenant)
            .FirstOrDefaultAsync(
                m => m.Id == id && m.Tenant!.Slug == tenantSlug,
                cancellationToken)
            .ConfigureAwait(false);

    private static TenantMemberDto ToDto(TenantMember member)
        => new(
            member.Id,
            member.Subject,
            member.DisplayName,
            member.Email,
            member.Role.ToString().ToLowerInvariant(),
            member.Status.ToString().ToLowerInvariant(),
            member.CreatedUtc,
            member.LastSeenUtc);
}

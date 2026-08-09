using Lakehold.Api.Auth;
using Lakehold.ControlPlane.Data;
using Lakehold.ControlPlane.Model;
using Lakehold.ControlPlane.Security;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

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

        tenants.MapPost("/{tenantSlug}/members", CreateAsync)
            .RequireCapability(Capability.TenantAdmin)
            .WithSummary("Creates an identity in the provider and admits it to this workspace.");

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

    /// <summary>Creates the identity and the membership together.</summary>
    /// <remarks>
    ///     <para>
    ///         Order matters and is not arbitrary. The identity is created first, because it is the
    ///         half Lakehold does not own and the half that can fail for reasons outside this process
    ///         — a name already taken, a rejected credential, an unreachable provider. Only once the
    ///         provider has committed does the membership row follow, keyed on the subject it just
    ///         reported. The reverse order would leave a membership pointing at nobody.
    ///     </para>
    ///     <para>
    ///         The residual failure is a created identity whose membership insert then fails. That
    ///         leaves a usable account with no access, which is the harmless direction: the person
    ///         signs in, reaches nothing, and appears here as a first arrival to be admitted
    ///         normally.
    ///     </para>
    /// </remarks>
    internal static async Task<Results<Ok<CreatedTenantMemberDto>, NotFound<string>, BadRequest<string>>> CreateAsync(
        string tenantSlug,
        CreateTenantMemberRequest request,
        ControlPlaneContext context,
        IUserProvisioner provisioner,
        IOptions<LakeholdOidcOptions> configured,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Username))
        {
            return TypedResults.BadRequest("A username is required.");
        }

        if (!provisioner.IsAvailable)
        {
            return TypedResults.BadRequest(
                "This node does not create users. Under SSO the people already exist in your "
                + "directory; add them there and they appear here once they sign in.");
        }

        var role = TokenRole.Reader;
        if (request.Role is { Length: > 0 } requested && !TryParseRole(requested, out role))
        {
            return TypedResults.BadRequest("Role must be owner, editor, or reader.");
        }

        var tenant = await context.Tenants
            .FirstOrDefaultAsync(t => t.Slug == tenantSlug, cancellationToken)
            .ConfigureAwait(false);
        if (tenant is null)
        {
            return TypedResults.NotFound($"Tenant '{tenantSlug}' was not found.");
        }

        ProvisionedUser created;
        try
        {
            created = await provisioner
                .CreateAsync(
                    new NewUserRequest(request.Username.Trim(), request.Email, request.DisplayName),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (UserProvisioningException failure)
        {
            // Written for the administrator reading it, and already stripped of anything the
            // provider echoed back.
            return TypedResults.BadRequest(failure.Message);
        }

        var member = new TenantMember
        {
            TenantId = tenant.Id,
            Tenant = tenant,
            Issuer = configured.Value.Authority,
            Subject = created.Subject,
            DisplayName = request.DisplayName ?? request.Username,
            Email = request.Email,
            Role = role,

            // Active immediately. An administrator who just typed somebody's name has made the
            // admission decision; asking them to approve the arrival they themselves created would
            // be ceremony, not a control.
            Status = MemberStatus.Active,
            CreatedUtc = clock.GetUtcNow(),
        };

        context.TenantMembers.Add(member);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return TypedResults.Ok(new CreatedTenantMemberDto(ToDto(member), created.TemporaryPassword));
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

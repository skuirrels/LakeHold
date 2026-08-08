using System.Security.Claims;
using Lakehold.ControlPlane.Data;
using Lakehold.ControlPlane.Model;
using Microsoft.EntityFrameworkCore;

namespace Lakehold.ControlPlane.Security;

/// <summary>The claim names an identity provider must emit, and the values LakeHold matches.</summary>
/// <param name="Issuer">
///     The configured authority. Taken from configuration rather than from an <c>iss</c> claim: the
///     cookie principal built after a browser login does not reliably carry one, and a deployment
///     only ever accepts tokens from the authority it was pointed at, so this is both the more
///     available answer and the more trustworthy one.
/// </param>
/// <param name="TenantClaim">Claim naming the tenant a first-time arrival should join.</param>
/// <param name="RoleClaim">Claim naming the role that arrival should hold.</param>
/// <param name="SystemAdminClaim">Claim carrying instance-administrator membership.</param>
/// <param name="SystemAdminValue">The value of that claim which grants it.</param>
public readonly record struct MemberClaimContract(
    string Issuer,
    string TenantClaim,
    string RoleClaim,
    string SystemAdminClaim,
    string SystemAdminValue);

/// <summary>
///     Resolves a signed-in person to what they may reach, from LakeHold's own membership records.
/// </summary>
/// <remarks>
///     LakeHold federates authentication and owns authorization. The provider proves who someone is;
///     this decides what that identity reaches, and it decides it from a row an administrator can
///     see, change, and revoke. Reading it straight from a claim — as this used to — meant access
///     could only be granted by editing the provider, could not be listed, and could not be revoked
///     from inside the product at all.
///     <para>
///         A tenant claim is still honoured, but only <em>once</em>, to admit a first-time arrival.
///         After that the membership row is authoritative: demoting someone in LakeHold is not
///         quietly undone the next time their provider re-asserts a stale role.
///     </para>
/// </remarks>
public sealed class MemberDirectory(ControlPlaneContext context, TimeProvider clock)
{
    /// <summary>
    ///     Resolves <paramref name="user"/> to a principal, or null when it reaches nothing.
    /// </summary>
    /// <remarks>
    ///     Null covers three different situations that must all behave identically to the caller: an
    ///     unauthenticated request, a signed-in person awaiting approval, and a suspended one.
    ///     Distinguishing them in the response would tell an outsider which subjects this deployment
    ///     knows about.
    /// </remarks>
    public async Task<LakeholdPrincipal?> ResolveAsync(
        ClaimsPrincipal? user,
        MemberClaimContract contract,
        CancellationToken cancellationToken = default)
    {
        if (user?.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        // Instance administration stays a provider assertion. It is an infrastructure capability
        // that provisions tenants, so it cannot be granted by a tenant membership without letting a
        // workspace owner promote themselves.
        if (contract.SystemAdminClaim.Length > 0
            && user.FindAll(contract.SystemAdminClaim)
                .Any(claim => string.Equals(claim.Value, contract.SystemAdminValue, StringComparison.Ordinal)))
        {
            return new LakeholdPrincipal(
                Scope: TokenScope.Instance,
                TenantId: null,
                TenantSlug: null,
                CatalogName: null,
                IsReadOnly: false,
                TokenId: null,
                Role: TokenRole.Owner,
                MemberId: null);
        }

        var issuer = contract.Issuer;
        var subject = Subject(user);
        if (issuer.Length == 0 || subject.Length == 0)
        {
            return null;
        }

        var member = await context.TenantMembers
            .Include(m => m.Tenant)
            .FirstOrDefaultAsync(
                m => m.Issuer == issuer && m.Subject == subject,
                cancellationToken)
            .ConfigureAwait(false);

        member ??= await AdmitAsync(user, contract, issuer, subject, cancellationToken).ConfigureAwait(false);

        if (member is null || member.Status != MemberStatus.Active || member.Tenant is null)
        {
            return null;
        }

        await RecordSeenAsync(member, cancellationToken).ConfigureAwait(false);

        return new LakeholdPrincipal(
            Scope: TokenScope.Tenant,
            TenantId: member.TenantId,
            TenantSlug: member.Tenant.Slug,
            CatalogName: null,
            IsReadOnly: member.Role == TokenRole.Reader,
            TokenId: null,
            Role: member.Role,
            MemberId: member.Id);
    }

    /// <summary>
    ///     Records a first-time arrival, active if their provider vouched for a tenant and pending
    ///     otherwise.
    /// </summary>
    /// <remarks>
    ///     The pending row is the point: an unrecognised person becomes visible to an administrator
    ///     instead of vanishing behind a refusal nobody can act on. It grants nothing.
    /// </remarks>
    private async Task<TenantMember?> AdmitAsync(
        ClaimsPrincipal user,
        MemberClaimContract contract,
        string issuer,
        string subject,
        CancellationToken cancellationToken)
    {
        var slug = contract.TenantClaim.Length > 0 ? user.FindFirst(contract.TenantClaim)?.Value : null;
        var tenant = slug is { Length: > 0 }
            ? await context.Tenants.FirstOrDefaultAsync(t => t.Slug == slug, cancellationToken).ConfigureAwait(false)
            : null;

        // Nothing to attach a pending membership to. A claim naming a tenant that does not exist is
        // indistinguishable from no claim at all, and inventing the tenant would be worse.
        if (tenant is null)
        {
            return null;
        }

        var member = new TenantMember
        {
            TenantId = tenant.Id,
            Tenant = tenant,
            Issuer = issuer,
            Subject = subject,
            DisplayName = DisplayName(user),
            Email = user.FindFirst(ClaimTypes.Email)?.Value ?? user.FindFirst("email")?.Value,
            Role = TokenRoleParser.Parse(
                contract.RoleClaim.Length > 0 ? user.FindFirst(contract.RoleClaim)?.Value : null,
                TokenRole.Reader),
            Status = MemberStatus.Active,
            CreatedUtc = clock.GetUtcNow(),
        };

        context.TenantMembers.Add(member);

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // Two tabs signing in at once race here. The unique index is what makes that safe, and
            // losing the race just means reading the row the winner wrote.
            context.Entry(member).State = EntityState.Detached;
            return await context.TenantMembers
                .Include(m => m.Tenant)
                .FirstOrDefaultAsync(m => m.Issuer == issuer && m.Subject == subject, cancellationToken)
                .ConfigureAwait(false);
        }

        return member;
    }

    private async Task RecordSeenAsync(TenantMember member, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();

        // Written at most hourly. Every request would otherwise turn an authorization read into a
        // write, on the hottest path in the product.
        if (member.LastSeenUtc is { } seen && now - seen < TimeSpan.FromHours(1))
        {
            return;
        }

        member.LastSeenUtc = now;
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The provider's stable identifier for this person, or empty when it emitted none.</summary>
    /// <remarks>
    ///     An identity with no subject cannot be recorded as a member, because every later sign-in
    ///     would look like a different person. Refusing is the only safe answer.
    /// </remarks>
    private static string Subject(ClaimsPrincipal user)
        => user.FindFirst("sub")?.Value
           ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value
           ?? string.Empty;

    private static string? DisplayName(ClaimsPrincipal user)
        => user.Identity?.Name
           ?? user.FindFirst("name")?.Value
           ?? user.FindFirst("preferred_username")?.Value
           ?? user.FindFirst("email")?.Value;
}

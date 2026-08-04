using Lakehold.ControlPlane.Security;

namespace Lakehold.Api.Auth;

/// <summary>Builds the claim contract <see cref="MemberDirectory"/> resolves against.</summary>
/// <remarks>
///     This replaced a resolver that read a tenant straight off a claim. Access now comes from a
///     membership record an administrator can list, change, and revoke; the claims below only name
///     the tenant a first-time arrival joins, and the group that grants instance administration.
/// </remarks>
public static class OidcPrincipal
{
    /// <summary>Projects configured OIDC options onto the contract the directory understands.</summary>
    public static MemberClaimContract ContractFor(LakeholdOidcOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return new MemberClaimContract(
            Issuer: options.Authority,
            TenantClaim: options.TenantClaim,
            RoleClaim: options.RoleClaim,
            SystemAdminClaim: options.SystemAdminClaim,
            SystemAdminValue: options.SystemAdminValue);
    }
}

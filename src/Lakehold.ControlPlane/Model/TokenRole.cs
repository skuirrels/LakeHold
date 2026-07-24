namespace Lakehold.ControlPlane.Model;

/// <summary>
///     What a credential may do within its tenant, orthogonal to which tenant or catalog it is scoped
///     to. Ordered most-privileged first so the default value of a freshly added column — <c>0</c> —
///     is <see cref="Owner"/>, which is what every credential minted before roles existed effectively
///     was.
/// </summary>
/// <remarks>
///     Read-only from phase 2 is the degenerate case of <see cref="Reader"/>: a reader's catalog is
///     attached read-only, so the capability is enforced by the engine, not by a per-statement check.
///     Maintenance and eject are <see cref="Owner"/> operations; ordinary writes are
///     <see cref="Editor"/>; querying is <see cref="Reader"/>. See <c>docs/AUTHENTICATION.md</c>.
/// </remarks>
public enum TokenRole
{
    /// <summary>Full control of the tenant: query, write, maintenance, eject, and token management.</summary>
    Owner = 0,

    /// <summary>Query and write, but not destructive maintenance or eject.</summary>
    Editor = 1,

    /// <summary>Read-only. The catalog is attached read-only, so writes fail in the engine.</summary>
    Reader = 2,
}

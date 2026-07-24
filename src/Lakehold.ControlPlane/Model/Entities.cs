using Lakehold.Engine.Catalog;

namespace Lakehold.ControlPlane.Model;

/// <summary>
///     An isolation boundary: an organisation, team, or environment. A tenant owns catalogs, and
///     a query always executes in exactly one tenant's context.
/// </summary>
public sealed class Tenant
{
    public int Id { get; set; }

    /// <summary>Stable URL-safe key used in API routes.</summary>
    public required string Slug { get; set; }

    public required string DisplayName { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }

    public ICollection<LakeCatalog> Catalogs { get; } = [];
}

/// <summary>
///     A DuckLake catalog belonging to a tenant. This is the control-plane record; the engine
///     turns it into a <see cref="CatalogDescriptor"/> when it attaches a compute session.
/// </summary>
public sealed class LakeCatalog
{
    public int Id { get; set; }

    public int TenantId { get; set; }

    /// <summary>
    ///     The name the catalog is attached as, and what tenants write in SQL. Constrained to a
    ///     bare SQL identifier because it reaches <c>ATTACH</c>, which cannot be parameterised.
    /// </summary>
    public required string Name { get; set; }

    public CatalogMetadataKind MetadataKind { get; set; } = CatalogMetadataKind.LocalFile;

    /// <summary>
    ///     File path, or a PostgreSQL connection string for a shared metadata catalog.
    /// </summary>
    /// <remarks>
    ///     A PostgreSQL value is a credential. Production deployments should store a secret
    ///     reference here and resolve it at attach time rather than persisting the password —
    ///     see <see cref="StorageSecretName"/>.
    /// </remarks>
    public required string MetadataSource { get; set; }

    /// <summary>Root URI for Parquet data files. Local path, or <c>s3://</c>, <c>gs://</c>, <c>az://</c>.</summary>
    public required string DataPath { get; set; }

    /// <summary>
    ///     Name of a DuckDB secret granting access to <see cref="DataPath"/>, created during session
    ///     start-up. Only the name is persisted; the credential itself never reaches this table.
    /// </summary>
    public string? StorageSecretName { get; set; }

    public bool IsReadOnly { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }

    public Tenant Tenant { get; set; } = null!;

    /// <summary>Projects this record into the descriptor the engine attaches.</summary>
    public CatalogDescriptor ToDescriptor() => new(
        Name,
        MetadataKind,
        MetadataSource,
        DataPath,
        StorageSecretName,
        IsReadOnly);
}

/// <summary>A named, reusable query.</summary>
public sealed class SavedQuery
{
    public int Id { get; set; }

    public int TenantId { get; set; }

    public required string Name { get; set; }

    public string? Description { get; set; }

    public required string Sql { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }

    public DateTimeOffset UpdatedUtc { get; set; }

    public Tenant Tenant { get; set; } = null!;
}

/// <summary>
///     An outbound change-data-capture subscription: a webhook fired whenever a catalog commits
///     snapshots that changed a subscribed table.
/// </summary>
/// <remarks>
///     <para>
///         The delivery cursor is <see cref="LastDeliveredSnapshot"/> — everything up to and
///         including it has been delivered. The dispatcher reads the next window from the cursor
///         plus one, so a change is delivered at least once and never re-delivered after a
///         successful post. Delivery is at-least-once, not exactly-once: a crash between the post
///         and the cursor write re-sends the window, which consumers must tolerate (the payload's
///         snapshot ids make deduplication cheap).
///     </para>
///     <para>
///         <see cref="Secret"/> signs payloads so the receiver can authenticate them. It is stored
///         here because the dispatcher must read it on every delivery, but it must never appear in
///         an API response or a log — the DTO layer omits it deliberately.
///     </para>
/// </remarks>
public sealed class ChangeSubscription
{
    public int Id { get; set; }

    public int TenantId { get; set; }

    /// <summary>Catalog whose changes are watched.</summary>
    public required string CatalogName { get; set; }

    /// <summary>
    ///     Table to watch, or null to watch every base table in the catalog.
    /// </summary>
    public string? TableName { get; set; }

    /// <summary>Schema of <see cref="TableName"/>. Defaults to <c>main</c>.</summary>
    public string SchemaName { get; set; } = "main";

    /// <summary>Endpoint the signed change payload is posted to.</summary>
    public required string EndpointUrl { get; set; }

    /// <summary>
    ///     Shared secret used to HMAC-sign each delivery. Never returned by the API and never
    ///     logged.
    /// </summary>
    public required string Secret { get; set; }

    /// <summary>Whether the dispatcher considers this subscription at all.</summary>
    public bool Active { get; set; } = true;

    /// <summary>
    ///     Highest snapshot id whose changes have been successfully delivered. Initialised to the
    ///     catalog's latest snapshot at creation, so a new subscription starts from "now" rather
    ///     than replaying the catalog's entire history into an unsuspecting endpoint.
    /// </summary>
    public long LastDeliveredSnapshot { get; set; }

    /// <summary>Consecutive delivery failures, reset on success. Drives observability, not retry.</summary>
    public int ConsecutiveFailures { get; set; }

    /// <summary>When the last delivery attempt happened, successful or not.</summary>
    public DateTimeOffset? LastAttemptUtc { get; set; }

    /// <summary>Failure message from the last attempt, cleared on success.</summary>
    public string? LastError { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }

    public Tenant Tenant { get; set; } = null!;
}

/// <summary>
///     One executed statement. Doubles as the audit log and as the source for the UI's history
///     panel, so it records both the outcome and the failure reason.
/// </summary>
public sealed class QueryRun
{
    public int Id { get; set; }

    public int TenantId { get; set; }

    public required string CatalogName { get; set; }

    public required string Sql { get; set; }

    public DateTimeOffset StartedUtc { get; set; }

    public double ElapsedMilliseconds { get; set; }

    public int RowCount { get; set; }

    public bool Succeeded { get; set; }

    /// <summary>Failure message when <see cref="Succeeded"/> is false.</summary>
    public string? Error { get; set; }

    /// <summary>
    ///     The <see cref="ApiToken"/> that ran the statement, for audit. Nullable — and no foreign key
    ///     — so the pre-auth history that predates tokens survives, and a revoked or deleted token
    ///     never takes its audit trail down with it.
    /// </summary>
    public int? TokenId { get; set; }

    public Tenant Tenant { get; set; } = null!;
}

/// <summary>What an <see cref="ApiToken"/> may do — its capability, kept distinct from its subject.</summary>
public enum TokenScope
{
    /// <summary>Acts as one tenant. The overwhelming majority of tokens.</summary>
    Tenant,

    /// <summary>
    ///     Provisions tenants and catalogs and mints tenant tokens. It cannot itself query, run
    ///     maintenance, or eject — every data path requires a tenant-scoped token, so the credential
    ///     always names the tenant whose data is reachable.
    /// </summary>
    Instance,
}

/// <summary>
///     A bearer credential for the HTTP API. The token string is shown once at creation and never
///     stored; only its <see cref="Prefix"/> and a SHA-256 <see cref="SecretHash"/> are persisted, so
///     a database read never yields a usable credential.
/// </summary>
/// <remarks>
///     Capability (<see cref="Scope"/>, <see cref="ReadOnly"/>) and subject (<see cref="TenantId"/>,
///     <see cref="CatalogName"/>) are separate axes — see <c>docs/AUTHENTICATION.md</c>. Generation and
///     verification live in <see cref="Security.ApiTokenFactory"/>.
/// </remarks>
public sealed class ApiToken
{
    public int Id { get; set; }

    /// <summary>Capability: acts as a tenant, or provisions the instance.</summary>
    public TokenScope Scope { get; set; }

    /// <summary>Owning tenant, or null for an instance-scoped token, which belongs to no tenant.</summary>
    public int? TenantId { get; set; }

    /// <summary>
    ///     Optional least-privilege narrowing for a tenant token: null grants every catalog in the
    ///     tenant, a value restricts the token to that one catalog. Subject, not capability — orthogonal
    ///     to <see cref="Scope"/>, and always null for an instance token.
    /// </summary>
    public string? CatalogName { get; set; }

    /// <summary>Human-facing label. Not a secret and not an identifier.</summary>
    public required string Name { get; set; }

    /// <summary>
    ///     The token's public prefix (<c>lkh_&lt;tenant&gt;_</c>, or <c>lkh_admin_</c>), stored in the
    ///     clear. It narrows a lookup to a candidate set before the secret is verified, and makes a
    ///     leaked token identifiable in a log without being usable.
    /// </summary>
    public required string Prefix { get; set; }

    /// <summary>SHA-256 of the full token, lower-hex. The token itself is never stored.</summary>
    public required string SecretHash { get; set; }

    /// <summary>Whether the credential produces a read-only catalog attachment.</summary>
    public bool ReadOnly { get; set; }

    /// <summary>
    ///     What the credential may do within its tenant. Defaults to <see cref="TokenRole.Owner"/>,
    ///     which is what every token minted before roles existed effectively was — so an existing
    ///     deployment's credentials keep working across the upgrade.
    /// </summary>
    public TokenRole Role { get; set; } = TokenRole.Owner;

    public DateTimeOffset CreatedUtc { get; set; }

    /// <summary>Optional expiry; a token past this instant is refused.</summary>
    public DateTimeOffset? ExpiresUtc { get; set; }

    /// <summary>Set when the token is revoked; a revoked token is refused thereafter.</summary>
    public DateTimeOffset? RevokedUtc { get; set; }

    /// <summary>Updated opportunistically, off the request path.</summary>
    public DateTimeOffset? LastUsedUtc { get; set; }

    /// <summary>Owning tenant, or null for an instance-scoped token.</summary>
    public Tenant? Tenant { get; set; }
}

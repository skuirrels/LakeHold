namespace Lakehold.Api.PgWire;

/// <summary>Configuration for the PostgreSQL wire-protocol endpoint.</summary>
/// <remarks>
///     See <c>docs/POSTGRES-WIRE.md</c> for the protocol surface and the connection model. The
///     defaults are deliberately closed: this opens a database port, so enabling it is a decision an
///     operator makes rather than one they inherit.
/// </remarks>
public sealed class PgWireOptions
{
    public const string SectionName = "Lakehold:PgWire";

    /// <summary>Whether the listener runs at all.</summary>
    public bool Enabled { get; set; }

    /// <summary>Listening port. 5433 rather than 5432 so it does not collide with a real PostgreSQL.</summary>
    public int Port { get; set; } = 5433;

    /// <summary>
    ///     Shared secret every connection must present. A secret: it belongs in <c>.env</c> as
    ///     <c>Lakehold__PgWire__Password</c>, never in <c>appsettings*.json</c>.
    /// </summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>
    ///     Permits starting with no password. Off by default so a misconfiguration fails closed
    ///     rather than publishing every tenant's catalog on a TCP port.
    /// </summary>
    public bool AllowAnonymous { get; set; }

    /// <summary>
    ///     Requests the password in the clear instead of MD5-salted, for clients that have dropped
    ///     MD5. Only meaningful behind TLS termination or on a trusted network.
    /// </summary>
    public bool AllowCleartextPassword { get; set; }

    /// <summary>
    ///     Optional ceiling on rows returned per statement. Zero — the default — streams without a
    ///     limit, because rows go straight to the socket and are never materialised.
    /// </summary>
    /// <remarks>
    ///     <see cref="Lakehold.Engine.Configuration.LakehouseOptions.MaxRowsPerResult"/> deliberately
    ///     does not apply here. It bounds a JSON response that must be built in memory before it is
    ///     sent; this path has no such moment. Silently handing a BI tool 10,000 rows of a 50,000-row
    ///     table would produce a confidently wrong report with nothing on screen to indicate it.
    /// </remarks>
    public int MaxRows { get; set; }

    /// <summary>
    ///     Path to the TLS certificate served to clients, as PKCS#12 (<c>.pfx</c>) or PEM. Unset
    ///     leaves the endpoint plaintext.
    /// </summary>
    /// <remarks>
    ///     Without this the shared password crosses the network protected only by MD5's per-
    ///     connection salt, which stops replay but not an observer who can see every byte of every
    ///     result set. A database port carrying tenant data off a trusted network needs TLS, and
    ///     terminating it in front of the process is only equivalent when the hop behind the
    ///     terminator is itself trusted.
    /// </remarks>
    public string TlsCertificatePath { get; set; } = string.Empty;

    /// <summary>Password protecting <see cref="TlsCertificatePath"/>. A secret: it belongs in <c>.env</c>.</summary>
    public string TlsCertificatePassword { get; set; } = string.Empty;

    /// <summary>
    ///     Private key file, when <see cref="TlsCertificatePath"/> is a PEM certificate rather than a
    ///     PKCS#12 bundle. Ignored for <c>.pfx</c> and <c>.p12</c>, which already carry the key.
    /// </summary>
    public string TlsCertificateKeyPath { get; set; } = string.Empty;

    /// <summary>
    ///     Refuses any client that will not negotiate TLS. Off by default because it breaks
    ///     plaintext clients; on, it is the setting that makes the guarantee real rather than
    ///     available — a client that simply declines otherwise gets a plaintext session.
    /// </summary>
    public bool RequireTls { get; set; }

    /// <summary>
    ///     Per-tenant passwords, keyed by tenant slug. When any are configured they are
    ///     authoritative and <see cref="Password"/> is ignored.
    /// </summary>
    /// <remarks>
    ///     <see cref="Password"/> authenticates the connection but not the tenant it named: any
    ///     holder of it can present themselves as any tenant, so on a multi-tenant node one
    ///     credential is every credential. These bind a secret to a single tenant, which is what
    ///     makes the isolation boundary meaningful from outside the process rather than only inside
    ///     it. Values are secrets and belong in <c>.env</c> as
    ///     <c>Lakehold__PgWire__TenantPasswords__demo</c>.
    /// </remarks>
    public Dictionary<string, string> TenantPasswords { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     Accepts a Lakehold API token as the password, verified against the same token store the
    ///     HTTP API uses — so revoking a credential closes the BI tool and the API together.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Off by default because it changes the authentication exchange: a hashed token store
    ///         deliberately cannot reproduce the plaintext that PostgreSQL's MD5 challenge requires, so
    ///         token authentication has to request the password in the clear and hash what it receives.
    ///         That is how most token-bearing database endpoints work, and it is safe only under TLS —
    ///         enabling this without <see cref="RequireTls"/> (or an explicit
    ///         <see cref="AllowCleartextPassword"/>) is refused at start-up rather than quietly putting
    ///         a credential on an unencrypted socket. SCRAM-SHA-256 with a stored verifier is the
    ///         better long-term answer and remains a follow-up.
    ///     </para>
    ///     <para>
    ///         Configured <see cref="TenantPasswords"/> keep working alongside it: a presented value
    ///         that looks like a token is verified against the store, anything else against the
    ///         configured password for that tenant.
    ///     </para>
    /// </remarks>
    public bool AllowTokenAuthentication { get; set; }

    /// <summary>Ceiling on concurrent client connections.</summary>
    public int MaxConnections { get; set; } = 64;

    /// <summary>
    ///     How long an unauthenticated connection may take to complete the startup handshake.
    /// </summary>
    /// <remarks>
    ///     Without this, a client that opens a socket and sends nothing holds a connection slot
    ///     indefinitely. <see cref="MaxConnections"/> of those deny the endpoint to everyone else at
    ///     the cost of one TCP handshake each — the classic slowloris shape, and the reason the
    ///     timeout is short and applies before authentication rather than after.
    /// </remarks>
    public TimeSpan HandshakeTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    ///     How long an established connection may sit between messages before it is closed.
    /// </summary>
    /// <remarks>
    ///     Generous by default because BI tools pool connections and legitimately leave them idle
    ///     between refreshes. It bounds the leak rather than policing usage: a client that
    ///     disappears without sending <c>Terminate</c> — a laptop closing, a container being killed —
    ///     otherwise holds its slot until the process restarts.
    /// </remarks>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    ///     Largest protocol message accepted from an authenticated client.
    /// </summary>
    /// <remarks>
    ///     The length prefix is attacker-controlled and is used to size an allocation, so it needs a
    ///     ceiling that reflects the largest legitimate message — a long statement — rather than the
    ///     protocol's theoretical maximum.
    /// </remarks>
    public int MaxMessageBytes { get; set; } = 16 * 1024 * 1024;
}

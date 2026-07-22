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

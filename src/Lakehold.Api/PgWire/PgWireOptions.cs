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
}

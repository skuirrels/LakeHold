namespace Lakehold.Api.Mcp;

/// <summary>Configuration for the Model Context Protocol endpoint.</summary>
/// <remarks>
///     See <c>docs/MCP.md</c> for the tool surface and what is deliberately withheld from an agent.
///     The defaults are closed, like <see cref="PgWire.PgWireOptions"/>: this opens a surface on which
///     an autonomous agent executes SQL, so enabling it is a decision an operator makes rather than
///     one they inherit by upgrading.
/// </remarks>
public sealed class McpOptions
{
    /// <summary>Configuration section binding this options object.</summary>
    public const string SectionName = "Lakehold:Mcp";

    /// <summary>Whether the MCP endpoint is served at all.</summary>
    public bool Enabled { get; set; }

    /// <summary>Route prefix the Streamable HTTP transport is mapped to.</summary>
    public string Route { get; set; } = "/mcp";

    /// <summary>
    ///     Ceiling on rows a tool returns, applied on top of
    ///     <c>LakehouseOptions.MaxRowsPerResult</c> and deliberately far below it. Zero or less
    ///     applies no MCP-specific ceiling, leaving only the engine's — the same convention
    ///     <see cref="PgWire.PgWireOptions.MaxRows"/> uses.
    /// </summary>
    /// <remarks>
    ///     Invariant 6 already requires a cap on a materialising path, and this is one — so this is
    ///     not a new rule, only a different number for a different budget. The HTTP cap bounds a JSON
    ///     response held in memory; this one bounds a language model's context window, and an agent
    ///     asking for a million rows is ordinary rather than anomalous. A truncated result says so, so
    ///     the agent narrows its query instead of silently reasoning about a prefix.
    /// </remarks>
    public int MaxRowsPerResult { get; set; } = 200;

    /// <summary>
    ///     Bounds a requested page size by <see cref="MaxRowsPerResult"/>, so every list-shaped tool
    ///     honours the same ceiling rather than only the query tool.
    /// </summary>
    /// <param name="requested">What the caller asked for.</param>
    /// <param name="engineCeiling">
    ///     The ceiling the equivalent HTTP route enforces, which still applies when no MCP-specific
    ///     one is configured.
    /// </param>
    /// <remarks>
    ///     A change feed page defaults to 1000 and admits 10000 over HTTP. Those are the right numbers
    ///     for a consumer writing to a database and the wrong ones for a context window, so a tool that
    ///     returned them would defeat the budget this option exists to keep.
    /// </remarks>
    internal int BoundPageSize(int requested, int engineCeiling)
    {
        var ceiling = MaxRowsPerResult > 0 ? Math.Min(MaxRowsPerResult, engineCeiling) : engineCeiling;
        return Math.Clamp(requested, 1, ceiling);
    }
}

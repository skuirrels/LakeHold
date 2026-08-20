using System.Reflection;

namespace Lakehold.Api.Mcp;

/// <summary>
///     What the server says about itself during the initialization handshake.
/// </summary>
/// <remarks>
///     <para>
///         MCP clients typically place <see cref="Instructions"/> in the model's system prompt, so
///         this is the one place to put knowledge that belongs to <em>no single tool</em> and would
///         otherwise have to be repeated in a dozen descriptions or, worse, learned by an agent
///         through failed calls. The SDK's own guidance is explicit that it should not duplicate what
///         tool descriptions already say.
///     </para>
///     <para>
///         The test of whether a line belongs here is whether an agent gets it wrong <em>before</em>
///         reading the relevant tool's description. Ordering ("call list_tenants first"), semantics
///         that span tools (a snapshot id from one is a bound for another), and traps that surface as
///         a confusing engine error rather than a validation message all qualify. Argument meanings
///         do not: those stay on the parameter.
///     </para>
/// </remarks>
internal static class McpServerDescription
{
    /// <summary>The assembly's product version, reported to clients as <c>serverInfo.version</c>.</summary>
    /// <remarks>
    ///     Read from the assembly rather than written here so it cannot drift from the build.
    ///     <c>Directory.Build.props</c> sets it, and a release may override it on the command line.
    ///     The informational version carries any source-link suffix, which is exactly what a client
    ///     log wants for a development build.
    /// </remarks>
    public static string Version { get; } =
        typeof(McpServerDescription).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(McpServerDescription).Assembly.GetName().Version?.ToString()
        ?? "0.0.0";

    /// <summary>Guidance sent to clients on initialization.</summary>
    public const string Instructions = """
        LakeHold is a multi-tenant lakehouse built on DuckDB and DuckLake. These tools read and, where
        an operator has enabled it, write a tenant's catalogs.

        Getting oriented
        - Call `list_tenants` first. Every other tool needs a tenant slug and a catalog name, and the
          ones it returns are the only valid values for this credential. Do not guess them, and do not
          take them from the user's prose without checking them against this list.
        - Then `describe_schema` (or read the `lakehold://{tenant}/{catalog}/schema` resource, which
          costs no tool call) before writing SQL. Table and column names here are frequently not what
          a name suggests.
        - SQL is DuckDB's dialect. `list_query_languages` reports what else this deployment accepts.

        Reading is always read-only
        - `query`, `query_snapshot`, and `execute_saved_query` attach the catalog read-only in the
          engine no matter what the credential could otherwise do. A write attempted through them
          fails in DuckDB, not in a check on the text you sent. Use `execute` to write, when it is
          offered.
        - The tool list is the deployment's answer about what it permits. If `execute` or the
          connector and maintenance tools are absent, an operator has turned them off; that is a
          setting, not a bug, and retrying will not help. Say so rather than working around it.

        Results are capped
        - Every list- and row-shaped result is bounded by a server ceiling tuned for a context window,
          well below what the HTTP API returns. A result that says it was truncated is a prefix: narrow
          the query, page with the cursor where one is offered, or aggregate in SQL instead of pulling
          rows and counting them yourself. Never present a truncated result as a complete answer.

        Time travel and change data
        - Snapshot ids come from `list_snapshots` and are the bounds the change and restore tools take.
        - Change ranges are INCLUSIVE at both ends. Having processed through snapshot L, ask from
          L + 1, or you will see L twice.
        - One update arrives as TWO changes sharing a `rowId`: `update_preimage` and
          `update_postimage`. Counting rows without accounting for that double-counts every update.

        Concurrency and safety
        - Saved queries, connectors, and maintenance use optimistic versions or snapshot fences. Read
          the current revision, version, or snapshot id, then pass it back. A conflict means someone
          else changed the thing while you were deciding — re-read and re-plan; never retry a stale one.
        - Destructive operations are two-step: plan, show the plan, then apply with the id the plan
          returned. Show the plan to the person before applying it.
        - Never put a password, token, or key into a connector definition. Those tools take a secret
          *reference* that an operator has bound; a literal value will be refused.

        When a catalog will not open
        - A catalog that has never been written to has no DuckLake metadata file yet, and a read-only
          attachment cannot create one. The engine reports a read-only open failure rather than an
          empty catalog. Report that the catalog needs one write from the Workbench; do not keep
          retrying.
        """;
}

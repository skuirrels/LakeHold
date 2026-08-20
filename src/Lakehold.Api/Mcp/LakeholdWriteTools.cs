using System.ComponentModel;
using Lakehold.ControlPlane.Data;
using Lakehold.ControlPlane.Model;
using Lakehold.ControlPlane.Security;
using Lakehold.Engine.Catalog;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Lakehold.Api.Mcp;

/// <summary>
///     The write tool, exposed only when the persisted runtime setting permits writes.
/// </summary>
/// <remarks>
///     <para>
///         A separate type, and a separate tool, rather than a mode switch inside <c>query</c>. The
///         reason is the tool annotations: MCP clients use <c>readOnly</c> and <c>destructive</c> to
///         decide whether to ask a human before calling, and those live in an attribute fixed at
///         compile time. A <c>query</c> that sometimes wrote would have to advertise itself as
///         read-only while writing, or as destructive while doing nothing of the kind. Either is a
///         client making a safety decision on false information.
///     </para>
///     <para>
///         Splitting them keeps every annotation true and gives a second property worth having: the
///         <em>tool list itself</em> says whether this deployment permits writes. An operator, or an
///         agent, can see it without reading configuration they have no access to.
///     </para>
///     <para>
///         Both gates must hold. The operator opts in with <see cref="McpOptions.AllowWrites"/>, and
///         the credential must not be read-only — and a read-only credential still produces a
///         read-only selected-catalog <em>attachment</em>, so a catalog-write refusal also comes from
///         DuckDB (invariants 4 and 20). The check gives a clear message. Neither mechanism replaces
///         the separate arbitrary-SQL containment boundary.
///     </para>
/// </remarks>
[McpServerToolType]
public sealed class LakeholdWriteTools(LakehouseService lakehouse, IHttpContextAccessor httpContextAccessor)
{
    /// <summary>Runs a statement that may modify a catalog.</summary>
    [McpServerTool(
        Name = "execute",
        Title = "Execute a writing statement",
        ReadOnly = false,
        Destructive = true,
        Idempotent = false, UseStructuredContent = true, OpenWorld = false)]
    [Description(
        "Runs a SQL statement that may modify data or schema — INSERT, UPDATE, DELETE, MERGE, CREATE, "
        + "ALTER, DROP. Requires a read-write credential. Use the query tool for reads: this one is "
        + "annotated destructive and a client may ask the user to confirm it.")]
    public async Task<McpExecuteResult> ExecuteAsync(
        [Description("Tenant slug the catalog belongs to.")] string tenant,
        [Description("Catalog to write to.")] string catalog,
        [Description("A single SQL statement.")] string sql,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            throw new McpException("A SQL statement is required.");
        }

        var principal = McpCaller.Authorize(httpContextAccessor, tenant, catalog);

        if (!McpCaller.Settings(httpContextAccessor).AllowWrites)
        {
            // The request filter protects direct calls made from a stale client tool cache. Keep a
            // second check here so any future transport or SDK dispatch change still fails closed.
            throw new McpException("Writing through MCP is disabled in LakeHold System Settings.");
        }

        if (principal.IsReadOnly)
        {
            // The engine would also refuse a selected-catalog write on the read-only attachment.
            // Saying so here gives the agent a clear credential error instead of a catalog error.
            throw new McpException(
                "This credential is read-only. Writing through MCP needs a read-write credential.");
        }

        return await McpFailure.GuardAsync(async () =>
        {
            var result = await lakehouse
                .ExecuteAsync(
                    tenant,
                    catalog,
                    sql,
                    cancellationToken,
                    readOnly: false,
                    principal.TokenId,
                    audit: QueryAuditContext.From(principal, QueryOrigin.Mcp))
                .ConfigureAwait(false);

            return new McpExecuteResult(result.RowsAffected, result.Rows.Count);
        }).ConfigureAwait(false);
    }
}

/// <summary>What a write reports back.</summary>
/// <param name="RowsAffected">
///     Rows the statement changed, where the engine could report it. Null for a statement whose
///     outcome has no count — DDL, or DML with <c>RETURNING</c>, which runs as a query (invariant 4).
/// </param>
/// <param name="RowsReturned">
///     Rows the statement produced, for a <c>RETURNING</c> clause. The rows themselves are deliberately
///     not echoed: a write tool that returned data would be a second, unannotated read path.
/// </param>
public sealed record McpExecuteResult(long? RowsAffected, int RowsReturned);

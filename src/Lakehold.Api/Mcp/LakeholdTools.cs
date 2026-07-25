using System.ComponentModel;
using Lakehold.Api.Auth;
using Lakehold.ControlPlane.Data;
using Lakehold.ControlPlane.Security;
using Lakehold.Engine.Catalog;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Lakehold.Api.Mcp;

/// <summary>The tools an agent may call. See <c>docs/MCP.md</c> for what is deliberately absent.</summary>
/// <remarks>
///     Each tool is a projection of an endpoint that already exists and enters the engine through the
///     same seam its HTTP route does — <see cref="LakehouseService"/> — rather than reimplementing it.
/// </remarks>
[McpServerToolType]
public sealed class LakeholdTools(
    LakehouseService lakehouse,
    IHttpContextAccessor httpContextAccessor,
    IOptions<McpOptions> options)
{
    /// <summary>Runs a read-only SQL query against one of a tenant's catalogs.</summary>
    /// <remarks>
    ///     <para>
    ///         The catalog is attached <b>read-only regardless of the credential's capability</b>. That
    ///         is stronger than the HTTP route, which honours the token, and it is the point: the
    ///         refusal comes from DuckDB rather than from a check applied to model-generated SQL
    ///         (invariants 4 and 20). Whether writes are ever offered here is a later decision taken on
    ///         evidence.
    ///     </para>
    ///     <para>
    ///         Capability is evaluated by the same <see cref="CapabilityPolicy"/> the HTTP route uses.
    ///         An unreachable tenant is reported as not-found rather than as forbidden, because a
    ///         forbidden would confirm it exists (invariant 19).
    ///     </para>
    /// </remarks>
    [McpServerTool(Name = "query", Title = "Query a Lakehold catalog", ReadOnly = true, Destructive = false)]
    [Description(
        "Runs a read-only SQL query against a Lakehold catalog and returns the rows. "
        + "DuckDB SQL dialect. Writes and DDL are rejected by the engine.")]
    public async Task<McpQueryResult> QueryAsync(
        [Description("Tenant slug the catalog belongs to.")] string tenant,
        [Description("Catalog to query.")] string catalog,
        [Description("A single SQL SELECT statement.")] string sql,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            throw new McpException("A SQL statement is required.");
        }

        var principal = Authorize(tenant, catalog);

        try
        {
            // readOnly: true unconditionally — see the remarks above.
            var result = await lakehouse
                .ExecuteAsync(tenant, catalog, sql, cancellationToken, readOnly: true, principal.TokenId)
                .ConfigureAwait(false);

            // Zero or less means no MCP-specific ceiling. Applying Take(0) instead would return an
            // empty result that claimed to be truncated, which reads as "the table is empty" to a
            // caller that cannot see the configuration.
            var cap = options.Value.MaxRowsPerResult;
            var rows = cap > 0 && result.Rows.Count > cap
                ? result.Rows.Take(cap).ToList()
                : result.Rows;

            return new McpQueryResult(
                [.. result.Columns.Select(c => new McpColumn(c.Name, c.DataType))],
                [.. rows],
                Truncated: result.Truncated || rows.Count < result.Rows.Count,
                RowCount: rows.Count);
        }
        catch (CatalogNotFoundException ex)
        {
            throw new McpException(ex.Message);
        }
        catch (DuckDB.NET.Data.DuckDBException ex)
        {
            // The engine's message names the offending token, which is exactly what lets an agent
            // correct its own SQL on the next call rather than guessing.
            throw new McpException(ex.Message);
        }
    }

    /// <summary>Resolves the principal and enforces the tool's capability, or throws.</summary>
    private ILakeholdPrincipal Authorize(string tenant, string catalog)
    {
        var http = httpContextAccessor.HttpContext
            ?? throw new McpException("This tool is only available over the HTTP transport.");

        var principal = http.GetLakeholdPrincipal();

        // McpAuthenticationFilter refuses a credential-less caller before any tool runs, so this holds
        // on every reachable path. Asserting it here means a future transport cannot quietly bypass it.
        if (!principal.IsAuthenticated)
        {
            throw new McpException("A credential is required.");
        }

        var decision = CapabilityPolicy.Evaluate(principal, Capability.TenantData, tenant, catalog);
        return decision.Outcome switch
        {
            CapabilityOutcome.Allowed => principal,
            CapabilityOutcome.Forbidden => throw new McpException(decision.Detail ?? "Forbidden."),
            _ => throw new McpException($"Catalog '{catalog}' was not found for tenant '{tenant}'."),
        };
    }
}

/// <summary>One result column, as an agent sees it.</summary>
/// <param name="Name">Column name.</param>
/// <param name="Type">
///     The engine's declared type. Included because it is what lets an agent write a correct
///     follow-up query — casting, comparing, or aggregating — rather than inferring a type from the
///     shape of the first row and getting it wrong on the second.
/// </param>
public sealed record McpColumn(string Name, string Type);

/// <summary>A tool's view of a query result: columns, rows, and whether it was cut short.</summary>
/// <param name="Columns">Columns, in ordinal order.</param>
/// <param name="Rows">Row values, each aligned to <paramref name="Columns"/> by ordinal.</param>
/// <param name="Truncated">
///     True when rows were withheld, by either the engine's cap or the tighter MCP one. An agent that
///     sees this should narrow its query rather than reason about a prefix as though it were the whole.
/// </param>
/// <param name="RowCount">Number of rows actually returned.</param>
public sealed record McpQueryResult(
    IReadOnlyList<McpColumn> Columns,
    IReadOnlyList<object?[]> Rows,
    bool Truncated,
    int RowCount);

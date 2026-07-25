using System.ComponentModel;
using Lakehold.Api.Auth;
using Lakehold.ControlPlane.Data;
using Lakehold.ControlPlane.Model;
using Lakehold.ControlPlane.Security;
using Lakehold.Engine.Catalog;
using Microsoft.EntityFrameworkCore;
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
    ControlPlaneContext controlPlane,
    IHttpContextAccessor httpContextAccessor,
    IOptions<McpOptions> options)
{
    /// <summary>Lists what the calling credential can reach: its tenants and their catalogs.</summary>
    /// <remarks>
    ///     The entry point for an agent, which otherwise has to be told the names in its prompt and
    ///     guesses wrongly when it is not. Scoped to the principal exactly as the HTTP listing route
    ///     is: an instance credential sees every tenant, a tenant credential sees its own.
    ///     <para>
    ///         It is <em>stricter</em> than the HTTP route in one respect. A catalog-narrowed
    ///         credential sees only the catalog it is narrowed to, rather than every catalog its
    ///         tenant owns. Listing catalogs the caller cannot query would waste its next call and
    ///         disclose names it has no use for; least privilege reads better here than parity.
    ///     </para>
    /// </remarks>
    [McpServerTool(Name = "list_tenants", Title = "List reachable tenants and catalogs", ReadOnly = true,
        Destructive = false)]
    [Description(
        "Lists the tenants and catalogs this credential can reach. Call this first: every other tool "
        + "needs a tenant and catalog name, and these are the only valid ones.")]
    public async Task<IReadOnlyList<McpTenant>> ListTenantsAsync(CancellationToken cancellationToken)
    {
        var principal = McpCaller.Principal(httpContextAccessor);

        var query = controlPlane.Tenants.AsNoTracking().Include(t => t.Catalogs).AsQueryable();
        if (principal.Scope == TokenScope.Tenant && principal.TenantSlug is { } slug)
        {
            query = query.Where(t => t.Slug == slug);
        }

        var narrowedTo = principal.CatalogName;

        var tenants = await query
            .OrderBy(t => t.Slug)
            .Select(t => new McpTenant(
                t.Slug,
                t.DisplayName,
                t.Catalogs
                    .Where(c => narrowedTo == null || c.Name == narrowedTo)
                    .OrderBy(c => c.Name)
                    .Select(c => new McpCatalog(c.Name, c.IsReadOnly))
                    .ToList()))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return tenants;
    }

    /// <summary>Returns a catalog's schemas, tables, and columns.</summary>
    /// <remarks>
    ///     DuckLake's own metadata tables are excluded — <c>CatalogBrowser</c> filters
    ///     <c>ducklake_*</c> at the source, which matters more here than in the workbench: a human
    ///     scrolls past twenty-eight internal tables, whereas an agent reads them and reasons about
    ///     them as though they were the tenant's data.
    /// </remarks>
    [McpServerTool(Name = "describe_schema", Title = "Describe a catalog's tables and columns",
        ReadOnly = true, Destructive = false)]
    [Description(
        "Returns the schemas, tables, and columns of a catalog, so a query can be written against "
        + "real column names and types rather than guessed ones.")]
    public async Task<IReadOnlyList<McpSchema>> DescribeSchemaAsync(
        [Description("Tenant slug the catalog belongs to.")] string tenant,
        [Description("Catalog to describe.")] string catalog,
        CancellationToken cancellationToken)
    {
        McpCaller.Authorize(httpContextAccessor, tenant, catalog);

        try
        {
            var schemas = await lakehouse.GetSchemasAsync(tenant, catalog, cancellationToken).ConfigureAwait(false);

            return
            [
                .. schemas.Select(s => new McpSchema(
                    s.Name,
                    [
                        .. s.Tables.Select(t => new McpTable(
                            t.Name,
                            t.Kind,
                            [.. t.Columns.Select(c => new McpSchemaColumn(c.Name, c.DataType, c.IsNullable))])),
                    ])),
            ];
        }
        catch (CatalogNotFoundException ex)
        {
            throw new McpException(ex.Message);
        }
    }

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

        var principal = McpCaller.Authorize(httpContextAccessor, tenant, catalog);

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

}

/// <summary>A tenant an agent may reach, with the catalogs it may reach inside it.</summary>
/// <param name="Tenant">Slug to pass as the <c>tenant</c> argument of every other tool.</param>
/// <param name="DisplayName">Human-readable name, for the agent's prose rather than its arguments.</param>
/// <param name="Catalogs">Catalogs reachable with this credential.</param>
public sealed record McpTenant(string Tenant, string DisplayName, IReadOnlyList<McpCatalog> Catalogs);

/// <summary>A catalog an agent may reach.</summary>
/// <param name="Catalog">Name to pass as the <c>catalog</c> argument of every other tool.</param>
/// <param name="ReadOnly">
///     Whether the catalog itself is read-only. Note this is a property of the catalog, not of the
///     session: the MCP surface attaches read-only either way, so a false here does not mean an agent
///     can write.
/// </param>
public sealed record McpCatalog(string Catalog, bool ReadOnly);

/// <summary>A schema and its tables.</summary>
public sealed record McpSchema(string Name, IReadOnlyList<McpTable> Tables);

/// <summary>A table and its columns. <paramref name="Kind"/> distinguishes a view from a base table.</summary>
public sealed record McpTable(string Name, string Kind, IReadOnlyList<McpSchemaColumn> Columns);

/// <summary>A column in a described table.</summary>
public sealed record McpSchemaColumn(string Name, string Type, bool Nullable);

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

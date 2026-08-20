using System.ComponentModel;
using Lakehold.Api.Querying;
using Lakehold.ControlPlane.Model;
using Lakehold.ControlPlane.Security;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Lakehold.Api.Mcp;

/// <summary>
///     The non-SQL query languages this deployment accepts, and a way to run one.
/// </summary>
/// <remarks>
///     <para>
///         LakeHold plans more than SQL — the saved-query tools have taken a <c>language</c> argument
///         since they shipped — but nothing on this surface said which values were legal, so an agent
///         either never used one or guessed an id and got a planning failure it could not interpret.
///         <c>list_query_languages</c> is the missing half of a parameter that already existed.
///     </para>
///     <para>
///         Deliberately a separate tool type from <see cref="LakeholdTools"/>. Planning a non-SQL
///         source calls an out-of-process compiler through <see cref="QueryExecutionCoordinator"/>,
///         and folding that dependency into the type that owns <c>query</c> would make plain SQL —
///         the overwhelmingly common case, and the one that must keep working when the compiler
///         container is down — depend on a service it never uses.
///     </para>
///     <para>
///         Execution is read-only like every other reading tool: the coordinator already forces a
///         read-only attachment for any language that is not SQL, and this tool passes
///         <c>callerReadOnly: true</c> on top of that, so the guarantee holds from both directions.
///     </para>
/// </remarks>
[McpServerToolType]
public sealed class LakeholdQueryLanguageTools(
    QueryExecutionCoordinator queries,
    IHttpContextAccessor httpContextAccessor)
{
    /// <summary>Reports the query languages this deployment can plan, and whether each is reachable.</summary>
    [McpServerTool(
        Name = "list_query_languages",
        Title = "List supported query languages",
        ReadOnly = true,
        Destructive = false,
        UseStructuredContent = true,
        OpenWorld = false)]
    [Description(
        "Lists the query languages this deployment accepts, with each one's id and whether it is "
        + "currently available. 'sql' is always present and is what the query tool runs. The ids here "
        + "are the only valid values for the language argument of query_language and the saved-query "
        + "tools. An unavailable language reports why; do not keep retrying it.")]
    public async Task<IReadOnlyList<McpQueryLanguage>> ListAsync(CancellationToken cancellationToken)
    {
        // Identity only. The language set is a property of the deployment, not of a tenant, and it
        // names no catalog — but the surface still requires a credential like every other (invariant 21).
        McpCaller.Principal(httpContextAccessor);

        var languages = await queries.GetLanguagesAsync(cancellationToken).ConfigureAwait(false);
        return
        [
            .. languages.Select(language => new McpQueryLanguage(
                language.Id,
                language.DisplayName,
                language.ReadOnly,
                language.SupportsSavedQueries,
                language.Available,
                language.UnavailableReason)),
        ];
    }

    /// <summary>Runs a source written in one of those languages.</summary>
    [McpServerTool(
        Name = "query_language",
        Title = "Run a non-SQL query",
        ReadOnly = true,
        Destructive = false,
        UseStructuredContent = true,
        OpenWorld = false)]
    [Description(
        "Runs a query written in one of the non-SQL languages list_query_languages reports, and "
        + "returns both the rows and the SQL it compiled to. Use the plain query tool for SQL — this "
        + "one adds a compilation step for no benefit there. Read-only, and bounded by the server's "
        + "MCP row ceiling.")]
    public Task<McpLanguageQueryResult> ExecuteAsync(
        [Description("Tenant slug the catalog belongs to.")] string tenant,
        [Description("Catalog to query.")] string catalog,
        [Description("Language id from list_query_languages.")] string language,
        [Description("The query source, in that language.")] string source,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            throw new McpException("A query source is required.");
        }

        if (string.IsNullOrWhiteSpace(language))
        {
            throw new McpException("A language id is required. Call list_query_languages for the valid ids.");
        }

        var principal = McpCaller.Authorize(httpContextAccessor, tenant, catalog);
        var settings = McpCaller.Settings(httpContextAccessor);

        return McpFailure.GuardAsync(async () =>
        {
            var execution = await queries.ExecuteAsync(
                    tenant,
                    catalog,
                    language,
                    source,
                    callerReadOnly: true,
                    QueryAuditContext.From(principal, QueryOrigin.Mcp),
                    recordHistory: true,
                    cancellationToken)
                .ConfigureAwait(false);

            var result = LakeholdTools.ToMcpQueryResult(execution.Result, settings.MaxRowsPerResult);
            return new McpLanguageQueryResult(
                language,
                execution.Plan.Sql,
                result.Columns,
                result.Rows,
                result.Truncated,
                result.RowCount);
        });
    }
}

/// <summary>One query language this deployment can plan.</summary>
/// <param name="Id">The value to pass as a <c>language</c> argument.</param>
/// <param name="ReadOnly">Whether the language can only ever produce a reading statement.</param>
/// <param name="SupportsSavedQueries">Whether a saved query may be written in it.</param>
/// <param name="Available">
///     Whether it can be planned right now. A language whose compiler is not reachable is reported
///     here rather than omitted, so an agent is told why instead of concluding it never existed.
/// </param>
public sealed record McpQueryLanguage(
    string Id,
    string DisplayName,
    bool ReadOnly,
    bool SupportsSavedQueries,
    bool Available,
    string? UnavailableReason);

/// <summary>A non-SQL query's result, with the SQL it compiled to.</summary>
/// <param name="GeneratedSql">
///     What actually ran. Returned so the agent can explain, verify, or reuse it — a compiled plan
///     the caller cannot see is one it cannot reason about.
/// </param>
public sealed record McpLanguageQueryResult(
    string Language,
    string GeneratedSql,
    IReadOnlyList<McpColumn> Columns,
    IReadOnlyList<object?[]> Rows,
    bool Truncated,
    int RowCount);

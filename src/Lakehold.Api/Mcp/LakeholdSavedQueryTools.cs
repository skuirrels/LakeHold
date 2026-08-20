using System.ComponentModel;
using Lakehold.ControlPlane.Data;
using Lakehold.ControlPlane.Model;
using Lakehold.ControlPlane.Security;
using ModelContextProtocol.Server;

namespace Lakehold.Api.Mcp;

/// <summary>Catalog-scoped saved-query authoring projected through the existing application service.</summary>
/// <remarks>
///     Every mutating tool here takes the <c>revision</c> it read, and the service rejects a stale
///     one. That is why the parameter descriptions say where the number comes from: an agent that
///     invents a revision gets a conflict it cannot explain, and an agent that reuses one from three
///     calls ago silently overwrites someone else's edit if the check is ever relaxed.
/// </remarks>
[McpServerToolType]
public sealed class LakeholdSavedQueryTools(
    SavedQueryService savedQueries,
    IHttpContextAccessor httpContextAccessor)
{
    [McpServerTool(Name = "list_saved_queries", Title = "List saved queries", ReadOnly = true, Destructive = false, UseStructuredContent = true, OpenWorld = false)]
    [Description(
        "Lists reusable query definitions in one catalog, with each one's current revision and whether "
        + "it is published as a catalog view. Check here before writing a query from scratch.")]
    public Task<IReadOnlyList<McpSavedQuery>> ListAsync(
        [Description("Tenant slug the catalog belongs to.")] string tenant,
        [Description("Catalog whose saved queries to list.")] string catalog,
        CancellationToken cancellationToken)
    {
        McpCaller.Authorize(httpContextAccessor, tenant, catalog);
        return McpFailure.GuardAsync<IReadOnlyList<McpSavedQuery>>(async () =>
            [.. (await savedQueries.ListAsync(tenant, catalog, cancellationToken).ConfigureAwait(false))
                .Select(ToDto)]);
    }

    [McpServerTool(Name = "get_saved_query", Title = "Get saved query", ReadOnly = true, Destructive = false, UseStructuredContent = true, OpenWorld = false)]
    [Description("Returns one saved query definition by id, including its source and current revision.")]
    public Task<McpSavedQuery> GetAsync(
        [Description("Tenant slug the catalog belongs to.")] string tenant,
        [Description("Catalog holding the saved query.")] string catalog,
        [Description("Saved query id, from list_saved_queries.")] int id,
        CancellationToken cancellationToken)
    {
        McpCaller.Authorize(httpContextAccessor, tenant, catalog);
        return ProjectAsync(() => savedQueries.GetAsync(tenant, catalog, id, cancellationToken));
    }

    [McpServerTool(Name = "create_saved_query", Title = "Create saved query", ReadOnly = false, Destructive = false, UseStructuredContent = true, OpenWorld = false)]
    [Description("Creates a catalog-scoped reusable query definition at revision one.")]
    public Task<McpSavedQuery> CreateAsync(
        [Description("Tenant slug the catalog belongs to.")] string tenant,
        [Description("Catalog to save the query in.")] string catalog,
        [Description("Name for the saved query. Must be unique within the catalog.")] string name,
        [Description("The query text, in the language named below.")] string source,
        CancellationToken cancellationToken,
        [Description("Query language id. Defaults to 'sql'; list_query_languages reports the alternatives.")]
        string language = "sql",
        [Description("Optional description of what the query answers.")] string? description = null)
    {
        var principal = McpCaller.AuthorizeForWrite(httpContextAccessor, tenant, catalog);
        return ProjectAsync(() => savedQueries.CreateAsync(
            tenant,
            catalog,
            name,
            description,
            source,
            language,
            principal.TokenId,
            cancellationToken));
    }

    [McpServerTool(Name = "update_saved_query", Title = "Update saved query", ReadOnly = false, Destructive = false, UseStructuredContent = true, OpenWorld = false)]
    [Description(
        "Replaces a saved query definition. The revision must be the one currently stored; a mismatch "
        + "means someone else edited it, so re-read it with get_saved_query rather than retrying.")]
    public Task<McpSavedQuery> UpdateAsync(
        [Description("Tenant slug the catalog belongs to.")] string tenant,
        [Description("Catalog holding the saved query.")] string catalog,
        [Description("Saved query id, from list_saved_queries.")] int id,
        [Description("The revision you just read. Rejected if the query has changed since.")] int revision,
        [Description("Replacement name.")] string name,
        [Description("Replacement query text, in the language named below.")] string source,
        CancellationToken cancellationToken,
        [Description("Query language id. Defaults to 'sql'; list_query_languages reports the alternatives.")]
        string language = "sql",
        [Description("Optional replacement description.")] string? description = null)
    {
        var principal = McpCaller.AuthorizeForWrite(httpContextAccessor, tenant, catalog);
        return ProjectAsync(() => savedQueries.UpdateAsync(
            tenant,
            catalog,
            id,
            revision,
            name,
            description,
            source,
            language,
            principal.TokenId,
            cancellationToken));
    }

    [McpServerTool(Name = "delete_saved_query", Title = "Delete saved query", ReadOnly = false, Destructive = true, OpenWorld = false)]
    [Description(
        "Deletes a saved query. Only an unpublished one can be deleted — unpublish it first if it "
        + "still owns a catalog view.")]
    public Task DeleteAsync(
        [Description("Tenant slug the catalog belongs to.")] string tenant,
        [Description("Catalog holding the saved query.")] string catalog,
        [Description("Saved query id, from list_saved_queries.")] int id,
        [Description("The revision you just read. Rejected if the query has changed since.")] int revision,
        CancellationToken cancellationToken)
    {
        McpCaller.AuthorizeForWrite(httpContextAccessor, tenant, catalog);
        return McpFailure.GuardAsync(() => savedQueries.DeleteAsync(
            tenant, catalog, id, revision, cancellationToken));
    }

    [McpServerTool(Name = "execute_saved_query", Title = "Execute saved query", ReadOnly = true, Destructive = false, UseStructuredContent = true, OpenWorld = false)]
    [Description(
        "Runs a saved definition and returns its rows, with the catalog attached read-only. Returns "
        + "the SQL the definition compiled to alongside the result, so a non-SQL definition is still "
        + "explainable. Bounded by the server's MCP row ceiling.")]
    public Task<McpSavedQueryResult> ExecuteAsync(
        [Description("Tenant slug the catalog belongs to.")] string tenant,
        [Description("Catalog holding the saved query.")] string catalog,
        [Description("Saved query id, from list_saved_queries.")] int id,
        CancellationToken cancellationToken)
    {
        var principal = McpCaller.Authorize(httpContextAccessor, tenant, catalog);
        return McpFailure.GuardAsync(async () =>
        {
            var execution = await savedQueries.ExecutePlannedAsync(
                tenant,
                catalog,
                id,
                principal.TokenId,
                recordHistory: true,
                cancellationToken,
                QueryAuditContext.From(principal, QueryOrigin.Mcp)).ConfigureAwait(false);
            var cap = McpCaller.Settings(httpContextAccessor).MaxRowsPerResult;
            var rows = cap > 0 ? execution.Result.Rows.Take(cap).ToArray() : execution.Result.Rows;
            return new McpSavedQueryResult(
                execution.Language,
                execution.Plan.Sql,
                [.. execution.Result.Columns.Select(column => new McpColumn(column.Name, column.DataType))],
                rows,
                execution.Result.Truncated || rows.Count < execution.Result.Rows.Count);
        });
    }

    [McpServerTool(Name = "publish_saved_query", Title = "Publish saved query", ReadOnly = false, Destructive = false, UseStructuredContent = true, OpenWorld = false)]
    [Description(
        "Publishes a saved query revision as a catalog view, so ordinary SQL can select from it. "
        + "The view is owned by the saved query and is dropped again by unpublish_saved_query.")]
    public Task<McpSavedQuery> PublishAsync(
        [Description("Tenant slug the catalog belongs to.")] string tenant,
        [Description("Catalog holding the saved query.")] string catalog,
        [Description("Saved query id, from list_saved_queries.")] int id,
        [Description("The revision you just read. Rejected if the query has changed since.")] int revision,
        [Description("Name for the catalog view this creates.")] string viewName,
        CancellationToken cancellationToken,
        [Description("Schema to create the view in. Defaults to 'main'.")] string schema = "main")
    {
        var principal = McpCaller.AuthorizeForWrite(httpContextAccessor, tenant, catalog);
        return ProjectAsync(() => savedQueries.PublishAsync(
            tenant,
            catalog,
            id,
            revision,
            schema,
            viewName,
            principal.TokenId,
            cancellationToken,
            QueryAuditContext.From(principal, QueryOrigin.Mcp)));
    }

    [McpServerTool(Name = "unpublish_saved_query", Title = "Unpublish saved query", ReadOnly = false, Destructive = true, UseStructuredContent = true, OpenWorld = false)]
    [Description(
        "Drops the catalog view a saved query owns and returns it to draft-only state. Anything "
        + "selecting from that view stops working.")]
    public Task<McpSavedQuery> UnpublishAsync(
        [Description("Tenant slug the catalog belongs to.")] string tenant,
        [Description("Catalog holding the saved query.")] string catalog,
        [Description("Saved query id, from list_saved_queries.")] int id,
        [Description("The revision you just read. Rejected if the query has changed since.")] int revision,
        CancellationToken cancellationToken)
    {
        var principal = McpCaller.AuthorizeForWrite(httpContextAccessor, tenant, catalog);
        return ProjectAsync(() => savedQueries.UnpublishAsync(
            tenant,
            catalog,
            id,
            revision,
            principal.TokenId,
            cancellationToken,
            QueryAuditContext.From(principal, QueryOrigin.Mcp)));
    }

    private static Task<McpSavedQuery> ProjectAsync(Func<Task<SavedQuery>> operation)
        => McpFailure.GuardAsync(async () => ToDto(await operation().ConfigureAwait(false)));

    private static McpSavedQuery ToDto(SavedQuery query) => new(
        query.Id,
        query.Name,
        query.Description,
        query.Sql,
        query.Language,
        query.Revision,
        query.PublishedSchema,
        query.PublishedViewName,
        query.PublishedRevision,
        query.CreatedUtc,
        query.UpdatedUtc);
}

/// <summary>One saved query definition.</summary>
/// <param name="Revision">
///     The optimistic version. Pass it back to any tool that changes this query; a mismatch means
///     someone else edited it first.
/// </param>
public sealed record McpSavedQuery(
    int Id,
    string Name,
    string? Description,
    string Source,
    string Language,
    int Revision,
    string? PublishedSchema,
    string? PublishedViewName,
    int? PublishedRevision,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc);

/// <summary>A saved query's result, with the SQL its source compiled to.</summary>
public sealed record McpSavedQueryResult(
    string Language,
    string GeneratedSql,
    IReadOnlyList<McpColumn> Columns,
    IReadOnlyList<IReadOnlyList<object?>> Rows,
    bool Truncated);

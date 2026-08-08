using System.ComponentModel;
using Lakehold.ControlPlane.Data;
using Lakehold.ControlPlane.Model;
using Lakehold.ControlPlane.Security;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Lakehold.Api.Mcp;

/// <summary>Catalog-scoped saved-query authoring projected through the existing application service.</summary>
[McpServerToolType]
public sealed class LakeholdSavedQueryTools(
    SavedQueryService savedQueries,
    IHttpContextAccessor httpContextAccessor)
{
    [McpServerTool(Name = "list_saved_queries", Title = "List saved queries", ReadOnly = true, Destructive = false)]
    [Description("Lists reusable query definitions in one catalog, including revision and publication state.")]
    public async Task<IReadOnlyList<McpSavedQuery>> ListAsync(
        string tenant,
        string catalog,
        CancellationToken cancellationToken)
    {
        McpCaller.Authorize(httpContextAccessor, tenant, catalog);
        try
        {
            return (await savedQueries.ListAsync(tenant, catalog, cancellationToken).ConfigureAwait(false))
                .Select(ToDto)
                .ToArray();
        }
        catch (CatalogNotFoundException exception)
        {
            throw new McpException(exception.Message);
        }
    }

    [McpServerTool(Name = "get_saved_query", Title = "Get saved query", ReadOnly = true, Destructive = false)]
    [Description("Returns one saved query definition by id.")]
    public async Task<McpSavedQuery> GetAsync(
        string tenant,
        string catalog,
        int id,
        CancellationToken cancellationToken)
    {
        McpCaller.Authorize(httpContextAccessor, tenant, catalog);
        return await ProjectAsync(
            () => savedQueries.GetAsync(tenant, catalog, id, cancellationToken)).ConfigureAwait(false);
    }

    [McpServerTool(Name = "create_saved_query", Title = "Create saved query", ReadOnly = false, Destructive = false)]
    [Description("Creates a catalog-scoped reusable query definition at revision one.")]
    public async Task<McpSavedQuery> CreateAsync(
        string tenant,
        string catalog,
        string name,
        string source,
        CancellationToken cancellationToken,
        string language = "sql",
        string? description = null)
    {
        var principal = McpCaller.AuthorizeForWrite(httpContextAccessor, tenant, catalog);
        return await ProjectAsync(() => savedQueries.CreateAsync(
            tenant,
            catalog,
            name,
            description,
            source,
            language,
            principal.TokenId,
            cancellationToken)).ConfigureAwait(false);
    }

    [McpServerTool(Name = "update_saved_query", Title = "Update saved query", ReadOnly = false, Destructive = false)]
    [Description("Replaces a saved query definition using its current optimistic revision.")]
    public async Task<McpSavedQuery> UpdateAsync(
        string tenant,
        string catalog,
        int id,
        int revision,
        string name,
        string source,
        CancellationToken cancellationToken,
        string language = "sql",
        string? description = null)
    {
        var principal = McpCaller.AuthorizeForWrite(httpContextAccessor, tenant, catalog);
        return await ProjectAsync(() => savedQueries.UpdateAsync(
            tenant,
            catalog,
            id,
            revision,
            name,
            description,
            source,
            language,
            principal.TokenId,
            cancellationToken)).ConfigureAwait(false);
    }

    [McpServerTool(Name = "delete_saved_query", Title = "Delete saved query", ReadOnly = false, Destructive = true)]
    [Description("Deletes an unpublished saved query using its current optimistic revision.")]
    public async Task DeleteAsync(
        string tenant,
        string catalog,
        int id,
        int revision,
        CancellationToken cancellationToken)
    {
        McpCaller.AuthorizeForWrite(httpContextAccessor, tenant, catalog);
        await HandleAsync(() => savedQueries.DeleteAsync(
            tenant, catalog, id, revision, cancellationToken)).ConfigureAwait(false);
    }

    [McpServerTool(Name = "execute_saved_query", Title = "Execute saved query", ReadOnly = true, Destructive = false)]
    [Description("Executes a saved definition through a structurally read-only catalog attachment.")]
    public async Task<McpSavedQueryResult> ExecuteAsync(
        string tenant,
        string catalog,
        int id,
        CancellationToken cancellationToken)
    {
        var principal = McpCaller.Authorize(httpContextAccessor, tenant, catalog);
        try
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
                execution.Result.Columns.Select(column => new McpColumn(column.Name, column.DataType)).ToArray(),
                rows,
                execution.Result.Truncated || rows.Count < execution.Result.Rows.Count);
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            throw new McpException(exception.Message);
        }
    }

    [McpServerTool(Name = "publish_saved_query", Title = "Publish saved query", ReadOnly = false, Destructive = false)]
    [Description("Publishes a saved query revision as a catalog view.")]
    public async Task<McpSavedQuery> PublishAsync(
        string tenant,
        string catalog,
        int id,
        int revision,
        string viewName,
        CancellationToken cancellationToken,
        string schema = "main")
    {
        var principal = McpCaller.AuthorizeForWrite(httpContextAccessor, tenant, catalog);
        return await ProjectAsync(() => savedQueries.PublishAsync(
            tenant,
            catalog,
            id,
            revision,
            schema,
            viewName,
            principal.TokenId,
            cancellationToken,
            QueryAuditContext.From(principal, QueryOrigin.Mcp))).ConfigureAwait(false);
    }

    [McpServerTool(Name = "unpublish_saved_query", Title = "Unpublish saved query", ReadOnly = false, Destructive = true)]
    [Description("Drops the catalog view owned by a saved query and returns it to draft-only state.")]
    public async Task<McpSavedQuery> UnpublishAsync(
        string tenant,
        string catalog,
        int id,
        int revision,
        CancellationToken cancellationToken)
    {
        var principal = McpCaller.AuthorizeForWrite(httpContextAccessor, tenant, catalog);
        return await ProjectAsync(() => savedQueries.UnpublishAsync(
            tenant,
            catalog,
            id,
            revision,
            principal.TokenId,
            cancellationToken,
            QueryAuditContext.From(principal, QueryOrigin.Mcp))).ConfigureAwait(false);
    }

    private static async Task<McpSavedQuery> ProjectAsync(Func<Task<SavedQuery>> operation)
    {
        try
        {
            return ToDto(await operation().ConfigureAwait(false));
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            throw new McpException(exception.Message);
        }
    }

    private static async Task HandleAsync(Func<Task> operation)
    {
        try
        {
            await operation().ConfigureAwait(false);
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            throw new McpException(exception.Message);
        }
    }

    private static bool IsExpected(Exception exception) =>
        exception is SavedQueryNotFoundException
            or SavedQueryValidationException
            or SavedQueryConflictException
            or CatalogNotFoundException
            or DuckDB.NET.Data.DuckDBException;

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

public sealed record McpSavedQueryResult(
    string Language,
    string GeneratedSql,
    IReadOnlyList<McpColumn> Columns,
    IReadOnlyList<IReadOnlyList<object?>> Rows,
    bool Truncated);

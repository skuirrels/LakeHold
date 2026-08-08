using System.ComponentModel;
using Lakehold.ControlPlane.Data;
using Lakehold.Engine.Catalog;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Lakehold.Api.Mcp;

/// <summary>Read-only physical-layer and audit projections for operational diagnosis.</summary>
[McpServerToolType]
public sealed class LakeholdInspectionTools(
    LakehouseService lakehouse,
    ControlPlaneContext controlPlane,
    IHttpContextAccessor httpContextAccessor)
{
    [McpServerTool(Name = "get_storage", Title = "Inspect catalog storage", ReadOnly = true, Destructive = false)]
    [Description("Returns table-by-table DuckLake storage footprint, file counts, deletes, and inlined rows.")]
    public Task<CatalogStorageInfo> GetStorageAsync(
        string tenant,
        string catalog,
        CancellationToken cancellationToken) =>
        ReadAsync(tenant, catalog, () => lakehouse.GetStorageAsync(tenant, catalog, cancellationToken));

    [McpServerTool(Name = "list_storage_files", Title = "List table storage files", ReadOnly = true, Destructive = false)]
    [Description("Lists the bounded physical data files backing a table, optionally at one retained snapshot.")]
    public Task<TableFileList> ListStorageFilesAsync(
        string tenant,
        string catalog,
        string table,
        CancellationToken cancellationToken,
        string schema = "main",
        long? snapshotId = null,
        int limit = 100) =>
        ReadAsync(
            tenant,
            catalog,
            () => lakehouse.GetTableFilesAsync(
                tenant,
                catalog,
                schema,
                table,
                snapshotId,
                McpCaller.Settings(httpContextAccessor).BoundPageSize(limit, 500),
                cancellationToken));

    [McpServerTool(Name = "get_table_detail", Title = "Inspect table detail", ReadOnly = true, Destructive = false)]
    [Description("Returns logical, storage, partition, and snapshot detail for one table.")]
    public Task<TableDetailInfo> GetTableDetailAsync(
        string tenant,
        string catalog,
        string table,
        CancellationToken cancellationToken,
        string schema = "main") =>
        ReadAsync(
            tenant,
            catalog,
            () => lakehouse.GetTableDetailAsync(tenant, catalog, schema, table, cancellationToken));

    [McpServerTool(Name = "get_table_profile", Title = "Profile table columns", ReadOnly = true, Destructive = false)]
    [Description("Profiles every column over live logical rows, optionally at one retained snapshot.")]
    public Task<TableProfileInfo> GetTableProfileAsync(
        string tenant,
        string catalog,
        string table,
        CancellationToken cancellationToken,
        string schema = "main",
        long? snapshotId = null) =>
        ReadAsync(
            tenant,
            catalog,
            () => lakehouse.GetTableProfileAsync(
                tenant, catalog, schema, table, snapshotId, cancellationToken));

    [McpServerTool(Name = "get_column_distribution", Title = "Inspect a column distribution", ReadOnly = true, Destructive = false)]
    [Description("Returns a bounded range or categorical distribution for one table column.")]
    public Task<ColumnDistributionInfo> GetColumnDistributionAsync(
        string tenant,
        string catalog,
        string table,
        string column,
        CancellationToken cancellationToken,
        string schema = "main",
        long? snapshotId = null,
        int maxBuckets = 20) =>
        ReadAsync(
            tenant,
            catalog,
            () => lakehouse.GetColumnDistributionAsync(
                tenant,
                catalog,
                schema,
                table,
                column,
                snapshotId,
                Math.Clamp(maxBuckets, 1, 100),
                cancellationToken));

    [McpServerTool(Name = "query_history", Title = "Read query audit history", ReadOnly = true, Destructive = false)]
    [Description("Lists recent query runs for one catalog, including actor and transport attribution.")]
    public async Task<IReadOnlyList<McpQueryRun>> QueryHistoryAsync(
        string tenant,
        string catalog,
        CancellationToken cancellationToken,
        int limit = 50)
    {
        McpCaller.Authorize(httpContextAccessor, tenant, catalog);
        var bounded = McpCaller.Settings(httpContextAccessor).BoundPageSize(limit, 200);

        return await controlPlane.QueryRuns
            .AsNoTracking()
            .Where(run => run.Tenant.Slug == tenant && run.CatalogName == catalog)
            .OrderByDescending(run => run.StartedUtc)
            .ThenByDescending(run => run.Id)
            .Take(bounded)
            .Select(run => new McpQueryRun(
                run.Id,
                run.Sql,
                run.Language,
                run.StartedUtc,
                run.ElapsedMilliseconds,
                run.RowCount,
                run.Succeeded,
                run.Error,
                run.ActorKind.ToString(),
                run.Origin.ToString(),
                run.MemberId != null
                    ? controlPlane.TenantMembers
                        .Where(member => member.Id == run.MemberId)
                        .Select(member => member.DisplayName)
                        .FirstOrDefault()
                    : controlPlane.ApiTokens
                        .Where(token => token.Id == run.TokenId)
                        .Select(token => token.Name)
                        .FirstOrDefault()))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<T> ReadAsync<T>(string tenant, string catalog, Func<Task<T>> read)
    {
        McpCaller.Authorize(httpContextAccessor, tenant, catalog);
        try
        {
            return await read().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is CatalogNotFoundException or ArgumentException)
        {
            throw new McpException(exception.Message);
        }
    }
}

public sealed record McpQueryRun(
    int Id,
    string Sql,
    string Language,
    DateTimeOffset StartedUtc,
    double ElapsedMilliseconds,
    int RowCount,
    bool Succeeded,
    string? Error,
    string ActorKind,
    string Origin,
    string? ActorName);

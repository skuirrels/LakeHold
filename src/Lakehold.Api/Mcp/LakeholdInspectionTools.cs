using System.ComponentModel;
using Lakehold.ControlPlane.Data;
using Lakehold.Engine.Catalog;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;

namespace Lakehold.Api.Mcp;

/// <summary>Read-only physical-layer and audit projections for operational diagnosis.</summary>
/// <remarks>
///     Every parameter carries a description. An agent cannot see a method signature, only the JSON
///     schema built from these attributes, so an undescribed <c>schema</c> or <c>limit</c> is a value
///     it has to guess — and the guess is what produces a confident, wrong call.
/// </remarks>
[McpServerToolType]
public sealed class LakeholdInspectionTools(
    LakehouseService lakehouse,
    ControlPlaneContext controlPlane,
    IHttpContextAccessor httpContextAccessor)
{
    [McpServerTool(Name = "get_storage", Title = "Inspect catalog storage", ReadOnly = true, Destructive = false, UseStructuredContent = true, OpenWorld = false)]
    [Description(
        "Returns table-by-table DuckLake storage footprint, file counts, deletes, and inlined rows. "
        + "Read from the catalog's own metadata, not by listing files, so it is exact rather than an "
        + "estimate. Use it to decide whether compaction or a flush is worth proposing.")]
    public Task<CatalogStorageInfo> GetStorageAsync(
        [Description("Tenant slug the catalog belongs to.")] string tenant,
        [Description("Catalog to inspect.")] string catalog,
        CancellationToken cancellationToken) =>
        ReadAsync(tenant, catalog, () => lakehouse.GetStorageAsync(tenant, catalog, cancellationToken));

    [McpServerTool(Name = "list_storage_files", Title = "List table storage files", ReadOnly = true, Destructive = false, UseStructuredContent = true, OpenWorld = false)]
    [Description(
        "Lists the physical Parquet data files backing one table, optionally as of a retained snapshot. "
        + "Bounded by the server's MCP page ceiling.")]
    public Task<TableFileList> ListStorageFilesAsync(
        [Description("Tenant slug the catalog belongs to.")] string tenant,
        [Description("Catalog holding the table.")] string catalog,
        [Description("Table whose data files to list.")] string table,
        CancellationToken cancellationToken,
        [Description("Schema holding the table. Defaults to 'main'.")] string schema = "main",
        [Description("Retained snapshot to list files as of. Omit for the current state. Ids come from list_snapshots.")]
        long? snapshotId = null,
        [Description("Maximum files to return. Bounded by the server's MCP page ceiling.")] int limit = 100) =>
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

    [McpServerTool(Name = "get_table_detail", Title = "Inspect table detail", ReadOnly = true, Destructive = false, UseStructuredContent = true, OpenWorld = false)]
    [Description(
        "Returns one table's logical shape, storage footprint, partitioning, and snapshot history in a "
        + "single call. Prefer this over several narrower calls when orienting on an unfamiliar table.")]
    public Task<TableDetailInfo> GetTableDetailAsync(
        [Description("Tenant slug the catalog belongs to.")] string tenant,
        [Description("Catalog holding the table.")] string catalog,
        [Description("Table to inspect.")] string table,
        CancellationToken cancellationToken,
        [Description("Schema holding the table. Defaults to 'main'.")] string schema = "main") =>
        ReadAsync(
            tenant,
            catalog,
            () => lakehouse.GetTableDetailAsync(tenant, catalog, schema, table, cancellationToken));

    [McpServerTool(Name = "get_table_profile", Title = "Profile table columns", ReadOnly = true, Destructive = false, UseStructuredContent = true, OpenWorld = false)]
    [Description(
        "Profiles every column of a table over live logical rows — null counts, distinct counts, and "
        + "ranges — optionally as of a retained snapshot. Cheaper and more reliable than writing your "
        + "own aggregate query per column.")]
    public Task<TableProfileInfo> GetTableProfileAsync(
        [Description("Tenant slug the catalog belongs to.")] string tenant,
        [Description("Catalog holding the table.")] string catalog,
        [Description("Table to profile.")] string table,
        CancellationToken cancellationToken,
        [Description("Schema holding the table. Defaults to 'main'.")] string schema = "main",
        [Description("Retained snapshot to profile as of. Omit to profile the current state.")]
        long? snapshotId = null) =>
        ReadAsync(
            tenant,
            catalog,
            () => lakehouse.GetTableProfileAsync(
                tenant, catalog, schema, table, snapshotId, cancellationToken));

    [McpServerTool(Name = "get_column_distribution", Title = "Inspect a column distribution", ReadOnly = true, Destructive = false, UseStructuredContent = true, OpenWorld = false)]
    [Description(
        "Returns a bounded distribution for one column: numeric and temporal columns come back as "
        + "ranged buckets, low-cardinality ones as categories with counts.")]
    public Task<ColumnDistributionInfo> GetColumnDistributionAsync(
        [Description("Tenant slug the catalog belongs to.")] string tenant,
        [Description("Catalog holding the table.")] string catalog,
        [Description("Table holding the column.")] string table,
        [Description("Column to summarise. Names come from describe_schema.")] string column,
        CancellationToken cancellationToken,
        [Description("Schema holding the table. Defaults to 'main'.")] string schema = "main",
        [Description("Retained snapshot to read as of. Omit for the current state.")] long? snapshotId = null,
        [Description("Maximum buckets or categories to return, from 1 to 100. Defaults to 20.")]
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

    [McpServerTool(Name = "query_history", Title = "Read query audit history", ReadOnly = true, Destructive = false, UseStructuredContent = true, OpenWorld = false)]
    [Description(
        "Lists recent query runs for one catalog, newest first, with the statement, its outcome and "
        + "duration, and which person or token ran it over which transport. Use it to see what has "
        + "already been asked before repeating work, or to explain a slow catalog.")]
    public Task<IReadOnlyList<McpQueryRun>> QueryHistoryAsync(
        [Description("Tenant slug the catalog belongs to.")] string tenant,
        [Description("Catalog whose history to read.")] string catalog,
        CancellationToken cancellationToken,
        [Description("Maximum runs to return, newest first. Bounded by the server's MCP page ceiling.")]
        int limit = 50)
    {
        McpCaller.Authorize(httpContextAccessor, tenant, catalog);
        var bounded = McpCaller.Settings(httpContextAccessor).BoundPageSize(limit, 200);

        // Guarded like every other tool. This one had no wrapper at all, so an EF or provider failure
        // reached the agent as a bare "an error occurred" with nothing to act on.
        return McpFailure.GuardAsync<IReadOnlyList<McpQueryRun>>(async () => await controlPlane.QueryRuns
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
            .ConfigureAwait(false));
    }

    private Task<T> ReadAsync<T>(string tenant, string catalog, Func<Task<T>> read)
    {
        McpCaller.Authorize(httpContextAccessor, tenant, catalog);
        return McpFailure.GuardAsync(read);
    }
}

/// <summary>One recorded query run, as an agent sees it.</summary>
/// <param name="ActorKind">Whether an API token or a signed-in person ran it; never both.</param>
/// <param name="Origin">Which surface it came from: Workbench, Rest, PgWire, Mcp, Import, or Connector.</param>
/// <param name="ActorName">The token's or member's display name, where the actor still exists.</param>
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

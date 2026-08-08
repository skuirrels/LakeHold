using Lakehold.ControlPlane.Data;
using Lakehold.Engine.Execution;
using Lakehold.Querying;
using Lakehold.ControlPlane.Security;

namespace Lakehold.Api.Querying;

/// <summary>Plans a Workbench source and funnels its SQL through the existing LakeHold execution boundary.</summary>
public sealed class QueryExecutionCoordinator(
    LakehouseService lakehouse,
    QuerySourcePlanningService planning)
{
    public Task<IReadOnlyList<QueryLanguageDescriptor>> GetLanguagesAsync(CancellationToken cancellationToken)
        => planning.GetLanguagesAsync(cancellationToken);

    public Task<QueryLanguageStarter> CreateStarterAsync(
        string tenant,
        string catalog,
        string language,
        CancellationToken cancellationToken)
        => planning.CreateStarterAsync(tenant, catalog, language, cancellationToken);

    public async Task<PlannedQueryResult> ExecuteAsync(
        string tenant,
        string catalog,
        string language,
        string source,
        bool callerReadOnly,
        QueryAuditContext audit,
        bool recordHistory,
        CancellationToken cancellationToken)
    {
        var plan = await planning.PlanAsync(tenant, catalog, language, source, cancellationToken)
            .ConfigureAwait(false);
        var parameters = QueryPlanParameterMapper.Decode(plan);
        var result = await lakehouse.ExecuteAsync(
            tenant,
            catalog,
            plan.Sql,
            cancellationToken,
            readOnly: callerReadOnly || !string.Equals(language, "sql", StringComparison.Ordinal),
            audit.TokenId,
            recordHistory,
            parameters,
            language,
            source,
            audit).ConfigureAwait(false);

        return new PlannedQueryResult(result, plan);
    }
}

public sealed record PlannedQueryResult(QueryResult Result, QueryPlan Plan);

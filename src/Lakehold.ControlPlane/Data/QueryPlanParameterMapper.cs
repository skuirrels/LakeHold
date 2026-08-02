using Lakehold.Engine.Execution;
using Lakehold.Querying;

namespace Lakehold.ControlPlane.Data;

public static class QueryPlanParameterMapper
{
    public static NamedQueryParameter[] Decode(QueryPlan plan)
        => [.. plan.Parameters.Select(parameter => new NamedQueryParameter(
            parameter.Name,
            QueryParameterCodec.Decode(parameter),
            parameter.DbType,
            parameter.IsNullable,
            parameter.Size,
            parameter.Precision,
            parameter.Scale))];
}

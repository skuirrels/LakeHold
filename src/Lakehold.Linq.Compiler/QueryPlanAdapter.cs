using System.Collections;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using DuckDB.EFCoreProvider.Extensions;
using Lakehold.Querying;

namespace Lakehold.Linq.Compiler;

/// <summary>Maps provider-owned command plans onto LakeHold's portable planner contract.</summary>
internal static class QueryPlanAdapter
{
    public static QueryPlan FromProviderPlan(DuckDBCommandPlan plan, string schemaFingerprint)
        => new(
            plan.CommandText,
            [.. plan.Parameters.Select(parameter => Encode(
                parameter.Name,
                parameter.ClrType,
                parameter.Value,
                parameter.DbType,
                parameter.IsNullable,
                parameter.Size,
                parameter.Precision,
                parameter.Scale))],
            [],
            schemaFingerprint);

    private static QueryParameter Encode(
        string name,
        Type declaredType,
        object? value,
        System.Data.DbType dbType,
        bool isNullable,
        int size,
        byte precision,
        byte scale)
    {
        var (contractType, snapshot) = PortableValue(declaredType, value);
        var serialized = snapshot is BigInteger bigInteger
            ? JsonSerializer.SerializeToElement(bigInteger.ToString(CultureInfo.InvariantCulture))
            : JsonSerializer.SerializeToElement(snapshot, snapshot?.GetType() ?? contractType);
        return new QueryParameter(
            name.TrimStart('$', '@', ':'),
            contractType.FullName ?? contractType.Name,
            serialized,
            dbType,
            isNullable,
            size,
            precision,
            scale);
    }

    private static (Type ContractType, object? Snapshot) PortableValue(Type declaredType, object? value)
    {
        if (value is null || value is string || value is byte[] || value is not IEnumerable values)
        {
            return (value?.GetType() ?? declaredType, value);
        }

        var elementType = declaredType.IsArray
            ? declaredType.GetElementType()
            : declaredType.GetInterfaces()
                .Append(declaredType)
                .FirstOrDefault(candidate => candidate.IsGenericType
                    && candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                ?.GetGenericArguments()[0];
        if (elementType is null)
        {
            return (value.GetType(), value);
        }

        var items = values.Cast<object?>().ToArray();
        var snapshot = Array.CreateInstance(elementType, items.Length);
        for (var index = 0; index < items.Length; index++)
        {
            snapshot.SetValue(items[index], index);
        }

        return (snapshot.GetType(), snapshot);
    }
}

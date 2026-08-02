using System.Globalization;
using System.Data;
using System.Numerics;
using System.Text.Json;

namespace Lakehold.Querying;

/// <summary>A query language that an installed Workbench planner can compile.</summary>
public sealed record QueryLanguageDescriptor(
    string Id,
    string DisplayName,
    string EditorLanguage,
    string StarterSource,
    bool ReadOnly,
    bool SupportsSavedQueries);

/// <summary>One catalog column supplied to a planner without any catalog credential.</summary>
public sealed record QueryColumnSchema(string Name, string StoreType, bool IsNullable);

/// <summary>One catalog table or view supplied to a planner.</summary>
public sealed record QueryTableSchema(
    string Schema,
    string Name,
    string Kind,
    IReadOnlyList<QueryColumnSchema> Columns);

/// <summary>A credential-free catalog shape supplied to a language plugin.</summary>
public sealed record QueryCatalogSchema(
    string SchemaFingerprint,
    IReadOnlyList<QueryTableSchema> Tables);

/// <summary>A catalog-aware starter expression produced by the language that owns its identifiers.</summary>
public sealed record QueryLanguageStarter(string Source, string SchemaFingerprint);

/// <summary>The complete input to an external query planner.</summary>
public sealed record QueryPlanningRequest(
    string Source,
    string SchemaFingerprint,
    IReadOnlyList<QueryTableSchema> Tables);

/// <summary>A source diagnostic suitable for editor markers.</summary>
public sealed record QueryDiagnostic(
    string Severity,
    string Code,
    string Message,
    int StartLine,
    int StartColumn,
    int EndLine,
    int EndColumn);

/// <summary>A serialized named parameter bound to provider-generated SQL.</summary>
public sealed record QueryParameter(
    string Name,
    string ClrType,
    JsonElement Value,
    DbType DbType,
    bool IsNullable,
    int Size = 0,
    byte Precision = 0,
    byte Scale = 0);

/// <summary>An executable, parameterized SQL plan produced from another query language.</summary>
public sealed record QueryPlan(
    string Sql,
    IReadOnlyList<QueryParameter> Parameters,
    IReadOnlyList<QueryDiagnostic> Diagnostics,
    string SchemaFingerprint);

/// <summary>Returned when source could not be planned.</summary>
public sealed record QueryPlanningFailure(IReadOnlyList<QueryDiagnostic> Diagnostics);

public sealed class QueryLanguageUnavailableException(string language)
    : Exception($"Query language '{language}' is not installed or is unavailable.");

public sealed class QuerySourceInvalidException(IReadOnlyList<QueryDiagnostic> diagnostics)
    : Exception("Query source could not be planned.")
{
    public IReadOnlyList<QueryDiagnostic> Diagnostics { get; } = diagnostics;
}

/// <summary>Language-neutral boundary implemented by the host's optional planner registry.</summary>
public interface IQuerySourcePlanner
{
    Task<IReadOnlyList<QueryLanguageDescriptor>> GetLanguagesAsync(CancellationToken cancellationToken);

    Task<QueryLanguageStarter> CreateStarterAsync(
        string language,
        QueryCatalogSchema catalogSchema,
        CancellationToken cancellationToken);

    Task<QueryPlan> PlanAsync(
        string language,
        QueryPlanningRequest planningRequest,
        CancellationToken cancellationToken);
}

/// <summary>Converts planner parameter payloads back to provider-compatible CLR values.</summary>
public static class QueryParameterCodec
{
    public static object? Decode(QueryParameter parameter)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        if (parameter.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (parameter.ClrType == "System.Byte[]")
        {
            return parameter.Value.GetBytesFromBase64();
        }

        if (parameter.ClrType.EndsWith("[]", StringComparison.Ordinal))
        {
            var elementType = parameter.ClrType[..^2];
            var runtimeType = Type.GetType(elementType, throwOnError: false)
                ?? throw new InvalidOperationException($"Planner array element type '{elementType}' is not supported.");
            var values = parameter.Value.EnumerateArray().ToArray();
            var result = Array.CreateInstance(runtimeType, values.Length);
            for (var index = 0; index < values.Length; index++)
            {
                result.SetValue(Decode(new QueryParameter(
                    parameter.Name,
                    elementType,
                    values[index],
                    parameter.DbType,
                    parameter.IsNullable)), index);
            }

            return result;
        }

        return parameter.ClrType switch
        {
            "System.Boolean" => parameter.Value.GetBoolean(),
            "System.Byte" => parameter.Value.GetByte(),
            "System.SByte" => parameter.Value.GetSByte(),
            "System.Int16" => parameter.Value.GetInt16(),
            "System.UInt16" => parameter.Value.GetUInt16(),
            "System.Int32" => parameter.Value.GetInt32(),
            "System.UInt32" => parameter.Value.GetUInt32(),
            "System.Int64" => parameter.Value.GetInt64(),
            "System.UInt64" => parameter.Value.GetUInt64(),
            "System.Single" => parameter.Value.GetSingle(),
            "System.Double" => parameter.Value.GetDouble(),
            "System.Decimal" => parameter.Value.GetDecimal(),
            "System.String" => parameter.Value.GetString(),
            "System.Guid" => parameter.Value.GetGuid(),
            "System.DateOnly" => DateOnly.Parse(parameter.Value.GetString()!, CultureInfo.InvariantCulture),
            "System.TimeOnly" => TimeOnly.Parse(parameter.Value.GetString()!, CultureInfo.InvariantCulture),
            "System.DateTime" => parameter.Value.GetDateTime(),
            "System.DateTimeOffset" => parameter.Value.GetDateTimeOffset(),
            "System.TimeSpan" => TimeSpan.Parse(parameter.Value.GetString()!, CultureInfo.InvariantCulture),
            "System.Numerics.BigInteger" => BigInteger.Parse(
                parameter.Value.ValueKind == JsonValueKind.String
                    ? parameter.Value.GetString()!
                    : parameter.Value.GetRawText(),
                CultureInfo.InvariantCulture),
            _ => throw new InvalidOperationException($"Planner parameter type '{parameter.ClrType}' is not supported."),
        };
    }
}

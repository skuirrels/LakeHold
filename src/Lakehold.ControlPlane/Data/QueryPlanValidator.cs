using System.Text.RegularExpressions;
using Lakehold.Engine.Execution;
using Lakehold.Querying;

namespace Lakehold.ControlPlane.Data;

/// <summary>Validates an external planner's portable plan before LakeHold executes it.</summary>
public sealed partial class QueryPlanValidator(LakehouseService lakehouse)
{
    private const int MaxGeneratedSqlLength = 1_000_000;
    private const int MaxParameters = 1_000;
    private const int MaxParameterJsonLength = 1_000_000;
    private const int MaxDiagnostics = 100;

    public async Task ValidateAsync(
        string tenant,
        string catalog,
        QueryPlan plan,
        string expectedSchemaFingerprint,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!string.Equals(plan.SchemaFingerprint, expectedSchemaFingerprint, StringComparison.Ordinal))
        {
            throw Rejected("The planner returned a plan for a different catalog schema.");
        }

        if (string.IsNullOrWhiteSpace(plan.Sql) || plan.Sql.Length > MaxGeneratedSqlLength)
        {
            throw Rejected($"Generated SQL must contain 1-{MaxGeneratedSqlLength:N0} characters.");
        }

        if (plan.Parameters is null || plan.Parameters.Count > MaxParameters)
        {
            throw Rejected($"A generated plan may contain at most {MaxParameters:N0} parameters.");
        }

        if (plan.Diagnostics is null || plan.Diagnostics.Count > MaxDiagnostics)
        {
            throw Rejected($"A generated plan may contain at most {MaxDiagnostics:N0} diagnostics.");
        }

        if (plan.Diagnostics.Any(diagnostic => diagnostic is null
                                               || diagnostic.Message is null
                                               || diagnostic.Message.Length > 10_000
                                               || diagnostic.Code is null
                                               || diagnostic.Code.Length > 100
                                               || diagnostic.StartLine < 1
                                               || diagnostic.StartColumn < 1
                                               || diagnostic.EndLine < diagnostic.StartLine
                                               || diagnostic.EndColumn < 1))
        {
            throw Rejected("Generated plan diagnostics are malformed or exceed their limits.");
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var parameter in plan.Parameters)
        {
            if (parameter is null
                || string.IsNullOrWhiteSpace(parameter.Name)
                || !ParameterName().IsMatch(parameter.Name)
                || !names.Add(parameter.Name))
            {
                throw Rejected("Planner parameter names must be unique C-style identifiers.");
            }

            if (string.IsNullOrWhiteSpace(parameter.ClrType) || parameter.ClrType.Length > 256)
            {
                throw Rejected($"Planner parameter '{parameter.Name}' has an invalid CLR type name.");
            }

            if (parameter.Value.GetRawText().Length > MaxParameterJsonLength)
            {
                throw Rejected($"Planner parameter '{parameter.Name}' exceeds the portable value limit.");
            }

            try
            {
                _ = QueryParameterCodec.Decode(parameter);
            }
            catch (Exception exception) when (exception is InvalidOperationException
                                               or FormatException
                                               or OverflowException
                                               or System.Text.Json.JsonException)
            {
                throw Rejected($"Planner parameter '{parameter.Name}' is not portable: {exception.Message}");
            }
        }

        var placeholders = ParameterPlaceholder().Matches(plan.Sql)
            .Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
        if (!placeholders.SetEquals(names))
        {
            throw Rejected("Generated SQL placeholders do not match the supplied named parameters.");
        }

        var statements = SqlStatementSplitter.Split(plan.Sql);
        if (statements.Count != 1
            || StatementVerb.Of(statements[0]) is not ("SELECT" or "WITH" or "VALUES"))
        {
            throw Rejected("An external planner must return exactly one read-producing statement.");
        }

        if (UnsafeExternalAccess().IsMatch(statements[0]))
        {
            throw Rejected("Generated SQL contains an external-access or dynamic-query function.");
        }

        if (!await lakehouse.IsReadQueryAsync(tenant, catalog, statements[0], cancellationToken).ConfigureAwait(false))
        {
            throw Rejected("Generated SQL is not a read query according to DuckDB's parser.");
        }
    }

    private static QueryPlanRejectedException Rejected(string message) => new(message);

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex ParameterName();

    [GeneratedRegex(@"(?<!\$)\$([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.CultureInvariant)]
    private static partial Regex ParameterPlaceholder();

    [GeneratedRegex(
        @"\b(?:read_csv|read_csv_auto|read_parquet|parquet_scan|read_json|read_json_auto|read_ndjson|read_blob|read_text|glob|read_xlsx|sqlite_scan|postgres_scan|mysql_scan|query|query_table|duckdb_secrets|which_secret)\s*\(",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UnsafeExternalAccess();
}

/// <summary>Raised when a configured planner violates LakeHold's executable-plan contract.</summary>
public sealed class QueryPlanRejectedException(string message) : Exception(message);

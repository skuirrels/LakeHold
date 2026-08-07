using Microsoft.OpenApi;

namespace Lakehold.Api.PublicApi;

/// <summary>Post-processes the generated document into a minimal, SDK-safe public contract.</summary>
public static class PublicApiOpenApi
{
    /// <summary>
    /// Keeps newly advertised access capabilities additive for existing generated clients. The
    /// server always emits these flags, but older clients must remain valid when they do not know
    /// about them and construct an <c>AccessDto</c> themselves.
    /// </summary>
    public static void PreserveAdditiveAccessCompatibility(OpenApiDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.Components?.Schemas?.TryGetValue("AccessDto", out var schema) == true
            && schema is OpenApiSchema accessSchema)
        {
            accessSchema.Required?.Remove("tenantAdmin");
        }
    }

    /// <summary>
    /// Replaces endpoint-inferred string error bodies with the canonical runtime problem contract.
    /// The public endpoint filter normalizes every 4xx/5xx result, so documenting the handler's
    /// pre-filter CLR union would make generated clients disagree with the wire response.
    /// </summary>
    public static void NormalizeProblemResponses(OpenApiDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        foreach (var response in document.Paths.Values
                     .SelectMany(path => path.Operations is null
                         ? Enumerable.Empty<OpenApiOperation>()
                         : path.Operations.Values)
                     .Where(operation => operation.Responses is not null)
                     .SelectMany(operation => operation.Responses!)
                     .Where(pair => int.TryParse(
                             pair.Key,
                             System.Globalization.NumberStyles.None,
                             System.Globalization.CultureInfo.InvariantCulture,
                             out var status)
                         && status >= StatusCodes.Status400BadRequest)
                     .Select(pair => pair.Value)
                     .OfType<OpenApiResponse>())
        {
            response.Content = new Dictionary<string, OpenApiMediaType>(StringComparer.Ordinal)
            {
                ["application/problem+json"] = new()
                {
                    Schema = new OpenApiSchemaReference("PublicApiProblemDetails", document),
                },
            };
        }
    }

    /// <summary>
    /// Removes the string alternative emitted for CLR numeric values. JSON request bodies do not
    /// accept quoted numbers, and query parameters remain correctly described by their parsed type.
    /// Several generators otherwise create unusable one-of wrapper models for defaulted integers.
    /// </summary>
    public static void NormalizeNumericSchemas(OpenApiDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        new OpenApiWalker(new NumericSchemaVisitor()).Walk(document);
    }

    /// <summary>Removes component schemas that are no longer reachable after private routes are removed.</summary>
    public static void PruneUnusedSchemas(OpenApiDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.Components?.Schemas is not { Count: > 0 } schemas)
        {
            return;
        }

        var reachable = CollectReferences(new OpenApiDocument { Paths = document.Paths });
        var previousCount = -1;
        while (previousCount != reachable.Count)
        {
            previousCount = reachable.Count;
            var referencedSchemas = schemas
                .Where(pair => reachable.Contains(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            var transitive = CollectReferences(new OpenApiDocument
            {
                Components = new OpenApiComponents { Schemas = referencedSchemas },
            });
            reachable.UnionWith(transitive);
        }

        foreach (var schema in schemas.Keys.Where(key => !reachable.Contains(key)).ToArray())
        {
            schemas.Remove(schema);
        }
    }

    private static HashSet<string> CollectReferences(OpenApiDocument document)
    {
        var visitor = new SchemaReferenceVisitor();
        new OpenApiWalker(visitor).Walk(document);
        return visitor.SchemaIds;
    }

    private sealed class SchemaReferenceVisitor : OpenApiVisitorBase
    {
        public HashSet<string> SchemaIds { get; } = new(StringComparer.Ordinal);

        public override void Visit(IOpenApiReferenceHolder referenceHolder)
        {
            if (referenceHolder is OpenApiSchemaReference
                {
                    Reference.Id: { Length: > 0 } id,
                })
            {
                SchemaIds.Add(id);
            }
        }
    }

    private sealed class NumericSchemaVisitor : OpenApiVisitorBase
    {
        public override void Visit(IOpenApiSchema schema)
        {
            if (schema is not OpenApiSchema mutable
                || mutable.Type is not { } types
                || (types & (JsonSchemaType.Integer | JsonSchemaType.Number)) == 0)
            {
                return;
            }

            if ((types & JsonSchemaType.String) != 0)
            {
                mutable.Type = types & ~JsonSchemaType.String;
            }

            // ASP.NET route constraints can leave their regular expression on the schema after the
            // value has been resolved as numeric. Pattern is a string-only keyword and causes SDK
            // generators to warn or emit contradictory validation.
            mutable.Pattern = null;
        }
    }
}

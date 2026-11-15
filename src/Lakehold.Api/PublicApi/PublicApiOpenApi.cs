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
    /// Keeps Avro connector response fields additive for clients generated from an older contract.
    /// The server emits the fields, but existing callers must still be able to construct and
    /// deserialize the response DTOs without supplying them.
    /// </summary>
    public static void PreserveAdditiveConnectorCompatibility(OpenApiDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        RemoveRequired(
            document,
            "DataConnectorAuthenticationDto",
            "schemaRegistryUsernameSecretReference",
            "schemaRegistryPasswordSecretReference");
        RemoveRequired(
            document,
            "DataConnectorDto",
            "sourceAcknowledgementPendingUtc",
            "sourceAcknowledgementError");
        RemoveRequired(
            document,
            "DataConnectorSourceSettingsDto",
            "kafkaBootstrapServers",
            "kafkaTopic",
            "kafkaConsumerGroup",
            "schemaRegistryUrl");
    }

    /// <summary>
    /// Keeps the new operator-command input optional so clients generated from the previous
    /// settings contract can still save the fields they know without disabling that tier.
    /// </summary>
    public static void PreserveAdditiveSystemSettingsCompatibility(OpenApiDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        RemoveRequired(document, "SystemSettingsDto", "mcpAllowOperatorCommands");
        RemoveRequired(document, "UpdateSystemSettingsRequest", "mcpAllowOperatorCommands");
    }

    /// <summary>
    /// Keeps new audit attribution fields additive for older generated clients. The server always
    /// emits them, while clients built from earlier contracts may construct history DTOs without
    /// knowing those fields exist.
    /// </summary>
    public static void PreserveAdditiveQueryAuditCompatibility(OpenApiDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        RemoveRequired(document, "QueryRunDto", "memberId", "actorKind", "actorName", "origin");
    }

    /// <summary>
    /// Keeps <c>ExecuteRequest.effectiveLanguage</c> nullable, as every published client has it.
    /// The property is computed and ignored on input, so the wire is unaffected either way; ASP.NET
    /// Core 11 began reading its non-nullable return type, which would otherwise change the generated
    /// Go, .NET, and Python models and remove the Go client's <c>SetEffectiveLanguageNil</c>.
    /// </summary>
    public static void PreserveExecuteRequestCompatibility(OpenApiDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.Components?.Schemas?.TryGetValue("ExecuteRequest", out var schema) == true
            && schema is OpenApiSchema { Properties: { } properties }
            && properties.TryGetValue("effectiveLanguage", out var property)
            && property is OpenApiSchema { Type: { } type } language)
        {
            language.Type = type | JsonSchemaType.Null;
        }
    }

    private static void RemoveRequired(OpenApiDocument document, string schemaName, params string[] properties)
    {
        if (document.Components?.Schemas?.TryGetValue(schemaName, out var schema) != true
            || schema is not OpenApiSchema mutable)
        {
            return;
        }

        foreach (var property in properties)
        {
            mutable.Required?.Remove(property);
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
            response.Content = new Dictionary<string, IOpenApiMediaType>(StringComparer.Ordinal)
            {
                ["application/problem+json"] = new OpenApiMediaType
                {
                    Schema = new OpenApiSchemaReference("PublicApiProblemDetails", document),
                },
            };
        }
    }

    /// <summary>
    /// Documents a cursor-paginated list as the cursor envelope alone. The handler returns the bare
    /// list, and <see cref="PublicApiPagination.WithCursorPagination{T}"/> declares the envelope that
    /// its filter always substitutes on <c>/api/v1</c>. ASP.NET Core 10 let the declared response
    /// replace the inferred one; 11 merges both into an <c>anyOf</c> of envelope and array, which no
    /// public request can receive and which turns every generated list method into a union.
    /// </summary>
    public static void NormalizeCursorPageResponses(OpenApiDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        foreach (var content in document.Paths.Values
                     .SelectMany(path => path.Operations is null
                         ? Enumerable.Empty<OpenApiOperation>()
                         : path.Operations.Values)
                     .Where(operation => operation.Responses is not null)
                     .SelectMany(operation => operation.Responses!)
                     .Where(pair => pair.Key.StartsWith('2'))
                     .Select(pair => pair.Value)
                     .OfType<OpenApiResponse>()
                     .Where(response => response.Content is not null)
                     .SelectMany(response => response.Content!.Values)
                     .OfType<OpenApiMediaType>())
        {
            if (content.Schema is not OpenApiSchema { AnyOf: { Count: > 1 } branches })
            {
                continue;
            }

            var envelopes = branches
                .OfType<OpenApiSchemaReference>()
                .Where(branch => branch.Reference.Id?.StartsWith("CursorPageOf", StringComparison.Ordinal) == true)
                .ToArray();
            if (envelopes.Length == 1)
            {
                content.Schema = envelopes[0];
            }
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

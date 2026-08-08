using Lakehold.Api.Auth;
using Lakehold.Api.Connectors;
using Lakehold.Api.Security;
using Lakehold.ControlPlane.Data;
using Lakehold.ControlPlane.Model;
using Lakehold.ControlPlane.Security;
using Lakehold.Api.PublicApi;
using Lakehold.Engine.Catalog;
using Microsoft.Extensions.Options;

namespace Lakehold.Api.Endpoints;

/// <summary>Thin HTTP administration for durable managed connectors and their run history.</summary>
public static class DataConnectorEndpoints
{
    public static void MapDataConnectorEndpoints(this RouteGroupBuilder tenants)
    {
        const string route = "/{tenantSlug}/catalogs/{catalogName}/connectors";
        tenants.MapGet(route, ListAsync)
            .RequireCapability(Capability.TenantOwner)
            .WithCursorPagination<DataConnectorDto>();
        tenants.MapGet(route + "/{id:int}", GetAsync)
            .RequireCapability(Capability.TenantOwner)
            .Produces<DataConnectorDto>();
        tenants.MapPost(route, CreateAsync)
            .RequireCapability(Capability.TenantOwner)
            .Produces<DataConnectorDto>(StatusCodes.Status201Created)
            .WithIdempotency();
        tenants.MapPut(route + "/{id:int}", UpdateAsync)
            .RequireCapability(Capability.TenantOwner)
            .Produces<DataConnectorDto>();
        tenants.MapDelete(route + "/{id:int}", DeleteAsync)
            .RequireCapability(Capability.TenantOwner)
            .Produces(StatusCodes.Status204NoContent);
        tenants.MapPost(route + "/{id:int}/run", RunAsync)
            .RequireCapability(Capability.TenantOwner)
            .Produces<DataConnectorExecutionDto>()
            .WithIdempotency();
        tenants.MapGet(route + "/{id:int}/runs", ListRunsAsync)
            .RequireCapability(Capability.TenantOwner)
            .WithCursorPagination<DataConnectorRunDto>();
        tenants.MapGet(route + "/{id:int}/dead-letters", ListDeadLettersAsync)
            .RequireCapability(Capability.TenantOwner)
            .WithCursorPagination<DataConnectorRunDto>();
        tenants.MapPost(route + "/{id:int}/pause", PauseAsync)
            .RequireCapability(Capability.TenantOwner)
            .Produces<DataConnectorDto>()
            .WithIdempotency();
        tenants.MapPost(route + "/{id:int}/resume", ResumeAsync)
            .RequireCapability(Capability.TenantOwner)
            .Produces<DataConnectorDto>()
            .WithIdempotency();
        tenants.MapPost(route + "/{id:int}/retry", RetryAsync)
            .RequireCapability(Capability.TenantOwner)
            .Produces<DataConnectorExecutionDto>()
            .WithIdempotency();
    }

    private static async Task<IResult> ListAsync(
        string tenantSlug,
        string catalogName,
        DataConnectorService connectors,
        CancellationToken cancellationToken)
    {
        var items = await connectors.ListAsync(tenantSlug, catalogName, cancellationToken).ConfigureAwait(false);
        return Results.Ok(items.Select(DataConnectorDto.From));
    }

    private static async Task<IResult> GetAsync(
        string tenantSlug,
        string catalogName,
        int id,
        DataConnectorService connectors,
        CancellationToken cancellationToken)
    {
        try
        {
            return Results.Ok(DataConnectorDto.From(
                await connectors.GetAsync(tenantSlug, catalogName, id, cancellationToken).ConfigureAwait(false)));
        }
        catch (CatalogNotFoundException ex)
        {
            return Results.NotFound(ex.Message);
        }
    }

    private static async Task<IResult> CreateAsync(
        string tenantSlug,
        string catalogName,
        DataConnectorDefinitionRequest request,
        DataConnectorService connectors,
        DataConnectorSourceResolver sources,
        IOptions<ConnectorOptions> options,
        CancellationToken cancellationToken)
    {
        var validation = await ValidateAsync(
                tenantSlug,
                catalogName,
                request,
                sources,
                options.Value,
                cancellationToken)
            .ConfigureAwait(false);
        if (validation.Error is not null)
        {
            return Results.BadRequest(validation.Error);
        }

        try
        {
            var connector = await connectors.CreateAsync(
                    tenantSlug,
                    catalogName,
                    validation.Definition!,
                    DateTimeOffset.UtcNow,
                    cancellationToken)
                .ConfigureAwait(false);
            return Results.Created(
                PublicApiRoutes.Canonical(
                    $"/tenants/{tenantSlug}/catalogs/{catalogName}/connectors/{connector.Id}"),
                DataConnectorDto.From(connector));
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Conflict(ex.Message);
        }
        catch (CatalogNotFoundException ex)
        {
            return Results.NotFound(ex.Message);
        }
        catch (DataConnectorConflictException ex)
        {
            return Results.Conflict(ex.Message);
        }
    }

    private static async Task<IResult> UpdateAsync(
        string tenantSlug,
        string catalogName,
        int id,
        UpdateDataConnectorRequest request,
        DataConnectorService connectors,
        DataConnectorSourceResolver sources,
        IOptions<ConnectorOptions> options,
        CancellationToken cancellationToken)
    {
        var validation = await ValidateAsync(
                tenantSlug,
                catalogName,
                request.Definition,
                sources,
                options.Value,
                cancellationToken)
            .ConfigureAwait(false);
        if (validation.Error is not null)
        {
            return Results.BadRequest(validation.Error);
        }

        try
        {
            var connector = await connectors.UpdateAsync(
                    tenantSlug,
                    catalogName,
                    id,
                    request.Version,
                    validation.Definition!,
                    DateTimeOffset.UtcNow,
                    cancellationToken)
                .ConfigureAwait(false);
            return Results.Ok(DataConnectorDto.From(connector));
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Conflict(ex.Message);
        }
        catch (CatalogNotFoundException ex)
        {
            return Results.NotFound(ex.Message);
        }
        catch (DataConnectorConflictException ex)
        {
            return Results.Conflict(ex.Message);
        }
    }

    private static async Task<IResult> DeleteAsync(
        string tenantSlug,
        string catalogName,
        int id,
        int version,
        DataConnectorService connectors,
        CancellationToken cancellationToken)
    {
        try
        {
            await connectors.DeleteAsync(tenantSlug, catalogName, id, version, cancellationToken).ConfigureAwait(false);
            return Results.NoContent();
        }
        catch (CatalogNotFoundException ex)
        {
            return Results.NotFound(ex.Message);
        }
        catch (DataConnectorConflictException ex)
        {
            return Results.Conflict(ex.Message);
        }
    }

    private static async Task<IResult> RunAsync(
        string tenantSlug,
        string catalogName,
        int id,
        DataConnectorService connectors,
        ConnectorRunner runner,
        CancellationToken cancellationToken)
    {
        try
        {
            var connector = await connectors.GetAsync(tenantSlug, catalogName, id, cancellationToken).ConfigureAwait(false);
            if (connector.SourceAcknowledgementPendingUtc is not null)
            {
                return Results.Conflict(
                    "The last published batch is awaiting source acknowledgement. Use the explicit retry operation to recover and replay it safely.");
            }
        }
        catch (CatalogNotFoundException ex)
        {
            return Results.NotFound(ex.Message);
        }

        var result = await runner.RunAsync(id, DataConnectorTrigger.Manual, cancellationToken).ConfigureAwait(false);
        return result is null
            ? Results.Conflict("The connector is already refreshing on another worker node.")
            : ToHttpResult(result);
    }

    internal static IResult ToHttpResult(ConnectorExecutionResult result)
    {
        var response = new DataConnectorExecutionDto(
                result.RunId,
                result.Status,
                result.RowsRead,
                result.RowsPublished,
                result.SourceVersion,
                result.Error);
        return result.FailureKind switch
        {
            ConnectorExecutionFailureKind.None => Results.Ok(response),
            ConnectorExecutionFailureKind.ClaimConflict => Results.Conflict(response),
            ConnectorExecutionFailureKind.Quality => Results.Json(response, statusCode: StatusCodes.Status422UnprocessableEntity),
            ConnectorExecutionFailureKind.TargetConflict => Results.Conflict(response),
            ConnectorExecutionFailureKind.Capacity => Results.Json(
                response,
                statusCode: StatusCodes.Status503ServiceUnavailable),
            ConnectorExecutionFailureKind.SourceOrImport => Results.Json(response, statusCode: StatusCodes.Status502BadGateway),
            ConnectorExecutionFailureKind.PublicationState => Results.Json(
                response,
                statusCode: StatusCodes.Status500InternalServerError),
            ConnectorExecutionFailureKind.SourceAcknowledgementPending => Results.Json(
                response,
                statusCode: StatusCodes.Status202Accepted),
            _ => throw new InvalidOperationException("Unknown connector execution outcome."),
        };
    }

    private static async Task<IResult> ListRunsAsync(
        string tenantSlug,
        string catalogName,
        int id,
        HttpContext http,
        int? limit,
        DataConnectorService connectors,
        CancellationToken cancellationToken)
    {
        try
        {
            var runs = await connectors.ListRunsAsync(
                    tenantSlug,
                    catalogName,
                    id,
                    http.IsLegacyApiRequest()
                        ? Math.Clamp(limit ?? 50, 1, PublicApiPagination.MaximumLimit)
                        : PublicApiPagination.RequiredSourceCount(http),
                    cancellationToken)
                .ConfigureAwait(false);
            return Results.Ok(runs.Select(DataConnectorRunDto.From));
        }
        catch (CatalogNotFoundException ex)
        {
            return Results.NotFound(ex.Message);
        }
    }

    private static async Task<IResult> ListDeadLettersAsync(
        string tenantSlug,
        string catalogName,
        int id,
        HttpContext http,
        int? limit,
        DataConnectorService connectors,
        CancellationToken cancellationToken)
    {
        try
        {
            var runs = await connectors.ListRunsAsync(
                    tenantSlug,
                    catalogName,
                    id,
                    DataConnectorRunStatus.DeadLettered,
                    http.IsLegacyApiRequest()
                        ? Math.Clamp(limit ?? 50, 1, PublicApiPagination.MaximumLimit)
                        : PublicApiPagination.RequiredSourceCount(http),
                    cancellationToken)
                .ConfigureAwait(false);
            return Results.Ok(runs.Select(DataConnectorRunDto.From));
        }
        catch (CatalogNotFoundException ex)
        {
            return Results.NotFound(ex.Message);
        }
    }

    private static Task<IResult> PauseAsync(
        string tenantSlug,
        string catalogName,
        int id,
        DataConnectorOperationRequest request,
        DataConnectorService connectors,
        CancellationToken cancellationToken) => ChangeStateAsync(
        () => connectors.PauseAsync(
            tenantSlug,
            catalogName,
            id,
            request.Version,
            DateTimeOffset.UtcNow,
            cancellationToken));

    private static Task<IResult> ResumeAsync(
        string tenantSlug,
        string catalogName,
        int id,
        DataConnectorOperationRequest request,
        DataConnectorService connectors,
        CancellationToken cancellationToken) => ChangeStateAsync(
        () => connectors.ResumeAsync(
            tenantSlug,
            catalogName,
            id,
            request.Version,
            resetFailures: false,
            now: DateTimeOffset.UtcNow,
            cancellationToken));

    private static async Task<IResult> RetryAsync(
        string tenantSlug,
        string catalogName,
        int id,
        DataConnectorOperationRequest request,
        DataConnectorService connectors,
        ConnectorRunner runner,
        CancellationToken cancellationToken)
    {
        try
        {
            _ = await connectors.ResumeAsync(
                    tenantSlug,
                    catalogName,
                    id,
                    request.Version,
                    resetFailures: true,
                    now: DateTimeOffset.UtcNow,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (CatalogNotFoundException ex)
        {
            return Results.NotFound(ex.Message);
        }
        catch (DataConnectorConflictException ex)
        {
            return Results.Conflict(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Conflict(ex.Message);
        }

        var result = await runner.RunAsync(id, DataConnectorTrigger.Manual, cancellationToken).ConfigureAwait(false);
        return result is null
            ? Results.Conflict("The connector retry was claimed by another worker node.")
            : ToHttpResult(result);
    }

    private static async Task<IResult> ChangeStateAsync(Func<Task<DataConnector>> operation)
    {
        try
        {
            return Results.Ok(DataConnectorDto.From(await operation().ConfigureAwait(false)));
        }
        catch (CatalogNotFoundException ex)
        {
            return Results.NotFound(ex.Message);
        }
        catch (DataConnectorConflictException ex)
        {
            return Results.Conflict(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Conflict(ex.Message);
        }
    }

    /// <summary>
    /// Builds the one approved connector definition.  The HTTP API and MCP control plane both call
    /// this boundary so an agent cannot create a connector the administrator UI could not save.
    /// </summary>
    internal static async Task<(DataConnectorDefinition? Definition, string? Error)> ValidateAsync(
        string tenantSlug,
        string catalogName,
        DataConnectorDefinitionRequest request,
        DataConnectorSourceResolver sources,
        ConnectorOptions options,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Kind)
            || request.Kind.All(char.IsDigit)
            || !Enum.TryParse<DataConnectorKind>(request.Kind, ignoreCase: true, out var kind)
            || !Enum.IsDefined(kind))
        {
            return (null, "Connector kind must be 'rest', 'grpc', 'postgresql', 'hubspot', or 'kafkaavro'.");
        }

        var format = request.RestResponseFormat?.Trim().ToLowerInvariant() switch
        {
            null or "" or "json-array" => RestResponseFormat.JsonArray,
            "ndjson" => RestResponseFormat.NewlineDelimitedJson,
            _ => (RestResponseFormat?)null,
        };
        if (format is null)
        {
            return (null, "REST response format must be 'json-array' or 'ndjson'.");
        }

        if (!Uri.TryCreate(request.EndpointUrl, UriKind.Absolute, out var endpoint))
        {
            return (null, "A connector endpoint must be an absolute URL.");
        }

        if (!string.IsNullOrEmpty(endpoint.UserInfo))
        {
            return (null, "Connector endpoints must not contain embedded credentials.");
        }

        if (kind == DataConnectorKind.PostgreSql
            && (!(endpoint.Scheme is "postgresql" or "postgres")
                || string.IsNullOrWhiteSpace(endpoint.AbsolutePath.Trim('/'))))
        {
            return (null, "PostgreSQL connector endpoints must use postgresql://host/database without embedded credentials.");
        }

        if (kind == DataConnectorKind.HubSpot
            && (endpoint.Scheme != Uri.UriSchemeHttps
                || !string.Equals(endpoint.DnsSafeHost, "api.hubapi.com", StringComparison.OrdinalIgnoreCase)))
        {
            return (null, "HubSpot connector endpoints must use https://api.hubapi.com.");
        }

        if (kind == DataConnectorKind.KafkaAvro
            && endpoint.Scheme != Uri.UriSchemeHttps)
        {
            return (null, "Kafka Avro connectors use an HTTPS Confluent-compatible schema registry endpoint.");
        }

        var policyEndpoint = kind == DataConnectorKind.PostgreSql
            ? new UriBuilder(Uri.UriSchemeHttps, endpoint.DnsSafeHost).Uri
            : endpoint;
        var resolution = await OutboundDestinationPolicy.ResolveAsync(
                policyEndpoint,
                options,
                "Connector",
                cancellationToken)
            .ConfigureAwait(false);
        if (resolution.Error is not null)
        {
            return (null, resolution.Error);
        }

        try
        {
            _ = SqlIdentifier.Quote(request.TargetSchema);
            _ = SqlIdentifier.Quote(request.TargetTable);
            foreach (var column in (request.RequiredColumns ?? []).Concat(request.NotNullColumns ?? []))
            {
                _ = SqlIdentifier.Quote(column);
            }


            foreach (var column in request.KeyColumns ?? [])
            {
                _ = SqlIdentifier.Quote(column);
            }
        }
        catch (ArgumentException ex)
        {
            return (null, ex.Message);
        }

        var platform = BuildPlatformDefinition(request, kind, sources);
        if (platform.Error is not null)
        {
            return (null, platform.Error);
        }

        if (kind == DataConnectorKind.KafkaAvro)
        {
            try
            {
                if (!options.TryGetKafkaEgressGateway(out _, out var gatewayError))
                {
                    return (null, gatewayError);
                }
                var kafkaDestinationError = await ValidateKafkaDestinationsAsync(
                        platform.Definition!.SourceSettings.KafkaBootstrapServers!,
                        new Uri(platform.Definition.SourceSettings.SchemaRegistryUrl!, UriKind.Absolute),
                        options,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (kafkaDestinationError is not null)
                {
                    return (null, kafkaDestinationError);
                }
            }
            catch (ArgumentException ex)
            {
                return (null, ex.Message);
            }
        }

        var credentialHosts = kind == DataConnectorKind.KafkaAvro
            ? GetKafkaBootstrapHosts(platform.Definition!.SourceSettings.KafkaBootstrapServers!)
                .Append(new Uri(platform.Definition.SourceSettings.SchemaRegistryUrl!, UriKind.Absolute).DnsSafeHost)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : [endpoint.DnsSafeHost];
        var unboundReference = ConnectorSecretAccessPolicy.References(platform.Definition!.Authentication)
            .FirstOrDefault(reference => credentialHosts.Any(host => !ConnectorSecretAccessPolicy.IsAllowed(
                options, tenantSlug, catalogName, reference, host)));
        if (unboundReference is not null)
        {
            return (null,
                "Every connector credential must have an operator-approved binding for this tenant, catalog, and every destination host.");
        }

        return (new DataConnectorDefinition(
            request.Name,
            request.Description,
            request.Owner,
            request.Tags ?? [],
            kind,
            endpoint.AbsoluteUri,
            request.CredentialEnvironmentVariable,
            format.Value,
            request.TargetSchema,
            request.TargetTable,
            request.MinimumRows,
            request.RequiredColumns ?? [],
            request.NotNullColumns ?? [],
            request.Enabled,
            request.RefreshIntervalSeconds,
            platform.Definition), null);
    }

    internal static (DataConnectorPlatformDefinition? Definition, string? Error) BuildPlatformDefinition(
        DataConnectorDefinitionRequest request,
        DataConnectorKind kind,
        DataConnectorSourceResolver sources)
    {
        var expectedAdapter = kind switch
        {
            DataConnectorKind.Rest => "lakehold.rest",
            DataConnectorKind.Grpc => "lakehold.grpc",
            DataConnectorKind.PostgreSql => "lakehold.postgresql",
            DataConnectorKind.HubSpot => "lakehold.hubspot-contacts",
            DataConnectorKind.KafkaAvro => "lakehold.kafka-avro",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        var adapterId = string.IsNullOrWhiteSpace(request.AdapterId) ? expectedAdapter : request.AdapterId.Trim();
        if (request.AdapterVersion < 1)
        {
            return (null, "Connector adapter versions must be positive.");
        }

        var manifest = sources.FindManifest(adapterId, request.AdapterVersion);
        if (manifest is null || manifest.ManifestVersion != 1)
        {
            return (null, $"Connector adapter '{adapterId}' version {request.AdapterVersion} is not registered with a compatible manifest.");
        }

        if (manifest.Kind != kind)
        {
            return (null, $"Connector adapter '{adapterId}' does not implement kind '{kind.ToString().ToLowerInvariant()}'.");
        }

        var defaultMode = manifest.ReadModes.Count == 1
            ? manifest.ReadModes.Single()
            : DataConnectorReadMode.FullSnapshot;
        var mode = request.ReadMode?.Trim().ToLowerInvariant() switch
        {
            null or "" => defaultMode,
            "full" or "full-snapshot" => DataConnectorReadMode.FullSnapshot,
            "incremental" => DataConnectorReadMode.Incremental,
            _ => (DataConnectorReadMode?)null,
        };
        if (mode is null || !manifest.ReadModes.Contains(mode.Value))
        {
            return (null, $"Adapter '{adapterId}' does not support the requested read mode.");
        }

        var schemaPolicy = request.SchemaPolicy?.Trim().ToLowerInvariant() switch
        {
            null or "" or "reject" => DataConnectorSchemaPolicy.Reject,
            "additive" => DataConnectorSchemaPolicy.Additive,
            "mapped-version" => DataConnectorSchemaPolicy.MappedVersion,
            _ => (DataConnectorSchemaPolicy?)null,
        };
        if (schemaPolicy is null)
        {
            return (null, "Schema policy must be 'reject', 'additive', or 'mapped-version'.");
        }

        var authentication = ParseAuthentication(request);
        if (authentication.Error is not null)
        {
            return (null, authentication.Error);
        }

        if (!manifest.AuthenticationKinds.Contains(authentication.Authentication!.Kind))
        {
            return (null, $"Adapter '{adapterId}' does not support the requested authentication kind.");
        }

        var settings = request.SourceSettings ?? new DataConnectorSourceSettingsRequest();
        if (settings.PageSize is < 1 or > 10_000)
        {
            return (null, "Connector page size must be between 1 and 10000.");
        }

        if (kind == DataConnectorKind.PostgreSql
            && (string.IsNullOrWhiteSpace(settings.SourceTable)
                || string.IsNullOrWhiteSpace(settings.CursorColumn)
                || settings.CursorType?.Trim().ToLowerInvariant() is not ("int64" or "timestamptz" or "uuid" or "text")
                || !settings.CursorIsCommitMonotonic))
        {
            return (null,
                "PostgreSQL connectors require sourceTable, cursorColumn, cursorType (int64, timestamptz, uuid, or text), and cursorIsCommitMonotonic=true.");
        }

        if (kind == DataConnectorKind.PostgreSql)
        {
            var tableParts = settings.SourceTable!.Split('.', StringSplitOptions.TrimEntries);
            try
            {
                if (tableParts.Length != 2)
                {
                    return (null, "PostgreSQL sourceTable must use schema.table notation.");
                }

                _ = SqlIdentifier.Quote(tableParts[0]);
                _ = SqlIdentifier.Quote(tableParts[1]);
                _ = SqlIdentifier.Quote(settings.CursorColumn!);
            }
            catch (ArgumentException ex)
            {
                return (null, ex.Message);
            }
        }

        if (kind == DataConnectorKind.HubSpot
            && (settings.PageSize > 200
                || (settings.Properties?.Count ?? 0) > 100
                || (settings.Properties ?? []).Any(property =>
                    string.IsNullOrWhiteSpace(property)
                    || property.Length > 255
                    || !property.All(character => char.IsAsciiLetterOrDigit(character) || character == '_'))))
        {
            return (null, "HubSpot page size cannot exceed 200 and properties must be at most 100 valid internal names.");
        }

        if (kind == DataConnectorKind.KafkaAvro
            && (string.IsNullOrWhiteSpace(settings.KafkaBootstrapServers)
                || string.IsNullOrWhiteSpace(settings.KafkaTopic)
                || string.IsNullOrWhiteSpace(settings.KafkaConsumerGroup)
                || string.IsNullOrWhiteSpace(settings.SchemaRegistryUrl)
                || !Uri.TryCreate(settings.SchemaRegistryUrl, UriKind.Absolute, out var registry)
                || registry.Scheme != Uri.UriSchemeHttps))
        {
            return (null,
                "Kafka Avro connectors require kafkaBootstrapServers, kafkaTopic, kafkaConsumerGroup, and an HTTPS schemaRegistryUrl.");
        }


        var mappings = new List<DataConnectorFieldMapping>();
        foreach (var mapping in request.FieldMappings ?? [])
        {
            if (mapping is null
                || string.IsNullOrWhiteSpace(mapping.Source)
                || string.IsNullOrWhiteSpace(mapping.Target)
                || string.IsNullOrWhiteSpace(mapping.Transform))
            {
                return (null, "Every field mapping requires non-empty source, target, and transform values.");
            }

            try
            {
                _ = SqlIdentifier.Quote(mapping.Source);
                _ = SqlIdentifier.Quote(mapping.Target);
            }
            catch (ArgumentException ex)
            {
                return (null, ex.Message);
            }

            var transform = mapping.Transform.Trim().ToLowerInvariant() switch
            {
                "none" => DataConnectorTransformKind.None,
                "trim" => DataConnectorTransformKind.Trim,
                "lowercase" => DataConnectorTransformKind.Lowercase,
                "uppercase" => DataConnectorTransformKind.Uppercase,
                "to-string" => DataConnectorTransformKind.ToString,
                _ => (DataConnectorTransformKind?)null,
            };
            if (transform is null)
            {
                return (null, "Field transforms must be none, trim, lowercase, uppercase, or to-string.");
            }

            mappings.Add(new DataConnectorFieldMapping(mapping.Source, mapping.Target, transform.Value));
        }

        if (schemaPolicy == DataConnectorSchemaPolicy.MappedVersion && mappings.Count == 0)
        {
            return (null, "Mapped-version schema policy requires at least one field mapping.");
        }

        if (schemaPolicy != DataConnectorSchemaPolicy.MappedVersion && mappings.Count > 0)
        {
            return (null, "Field mappings require mapped-version schema policy.");
        }

        var keys = request.KeyColumns ?? [];
        if (mode == DataConnectorReadMode.Incremental && keys.Count == 0)
        {
            return (null, "Incremental connectors require at least one key column.");
        }

        return (new DataConnectorPlatformDefinition(
            adapterId,
            request.AdapterVersion,
            mode.Value,
            schemaPolicy.Value,
            keys,
            mappings,
            new DataConnectorSourceSettings(
                settings.SourceTable,
                settings.CursorColumn,
                settings.CursorType,
                settings.PageSize,
                settings.Properties,
                settings.CursorIsCommitMonotonic,
                settings.KafkaBootstrapServers,
                settings.KafkaTopic,
                settings.KafkaConsumerGroup,
                settings.SchemaRegistryUrl),
            authentication.Authentication!,
            request.MaxAttempts,
            request.RetryBaseSeconds,
            request.RetryMaxSeconds), null);
    }

    internal static async Task<string?> ValidateKafkaDestinationsAsync(
        string bootstrapServers,
        Uri registry,
        ConnectorOptions options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = GetKafkaBootstrapHosts(bootstrapServers);
        return options.TryGetKafkaEgressGateway(out _, out var error) ? null : error;
    }

    internal static IReadOnlyList<string> GetKafkaBootstrapHosts(string bootstrapServers)
    {
        var entries = bootstrapServers.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (entries.Length == 0 || entries.Length > 32)
        {
            throw new ArgumentException("Kafka bootstrap servers must contain between one and 32 host:port entries.");
        }

        var hosts = new List<string>(entries.Length);
        foreach (var entry in entries)
        {
            if (!Uri.TryCreate($"kafka://{entry}", UriKind.Absolute, out var endpoint)
                || endpoint.Port is < 1 or > 65535
                || string.IsNullOrWhiteSpace(endpoint.DnsSafeHost)
                || !string.IsNullOrEmpty(endpoint.UserInfo)
                || endpoint.AbsolutePath != "/")
            {
                throw new ArgumentException("Kafka bootstrap servers must be comma-separated host:port entries without credentials or paths.");
            }
            hosts.Add(endpoint.DnsSafeHost);
        }
        return hosts;
    }

    private static (DataConnectorAuthentication? Authentication, string? Error) ParseAuthentication(
        DataConnectorDefinitionRequest request)
    {
        var input = request.Authentication;
        if (input is not null && request.CredentialEnvironmentVariable is { Length: > 0 })
        {
            return (null, "Use authentication secret references instead of combining them with the legacy credential environment variable.");
        }

        if (input is null && request.CredentialEnvironmentVariable is { Length: > 0 } legacy)
        {
            return (new DataConnectorAuthentication(
                DataConnectorAuthenticationKind.Bearer,
                $"env://{legacy}"), null);
        }

        input ??= new DataConnectorAuthenticationRequest();
        if (string.IsNullOrWhiteSpace(input.Kind))
        {
            return (null, "Connector authentication kind is required.");
        }

        var kind = input.Kind.Trim().ToLowerInvariant() switch
        {
            "none" => DataConnectorAuthenticationKind.None,
            "bearer" => DataConnectorAuthenticationKind.Bearer,
            "oauth-refresh-token" => DataConnectorAuthenticationKind.OAuthRefreshToken,
            "mtls" => DataConnectorAuthenticationKind.MutualTls,
            "custom-header" => DataConnectorAuthenticationKind.CustomHeader,
            "postgresql-password" => DataConnectorAuthenticationKind.PostgreSqlPassword,
            "kafka-sasl-plain" => DataConnectorAuthenticationKind.KafkaSaslPlain,
            _ => (DataConnectorAuthenticationKind?)null,
        };
        if (kind is null)
        {
            return (null, "Connector authentication kind is not approved.");
        }

        var references = new[]
        {
            input.SecretReference,
            input.UsernameSecretReference,
            input.PasswordSecretReference,
            input.ClientIdSecretReference,
            input.ClientSecretReference,
            input.RefreshTokenSecretReference,
            input.ClientCertificateSecretReference,
            input.CertificatePasswordSecretReference,
            input.SchemaRegistryUsernameSecretReference,
            input.SchemaRegistryPasswordSecretReference,
        };
        if (references.Where(value => value is not null).Any(value =>
                !(value!.StartsWith("env://", StringComparison.OrdinalIgnoreCase)
                  || value.StartsWith("vault://", StringComparison.OrdinalIgnoreCase))
                || value.Length > 1_024
                || value.EndsWith("://", StringComparison.Ordinal)))
        {
            return (null, "Secret references must use the env:// or vault:// provider scheme.");
        }

        var requiredError = kind.Value switch
        {
            DataConnectorAuthenticationKind.Bearer when string.IsNullOrWhiteSpace(input.SecretReference) =>
                "Bearer authentication requires secretReference.",
            DataConnectorAuthenticationKind.CustomHeader when string.IsNullOrWhiteSpace(input.SecretReference) =>
                "Custom-header authentication requires secretReference.",
            DataConnectorAuthenticationKind.CustomHeader when input.CustomHeaderName is not ("X-Api-Key" or "Api-Key") =>
                "Custom authentication header must be X-Api-Key or Api-Key.",
            DataConnectorAuthenticationKind.MutualTls when string.IsNullOrWhiteSpace(input.ClientCertificateSecretReference) =>
                "mTLS authentication requires clientCertificateSecretReference.",
            DataConnectorAuthenticationKind.OAuthRefreshToken when string.IsNullOrWhiteSpace(input.ClientIdSecretReference)
                                                                  || string.IsNullOrWhiteSpace(input.ClientSecretReference)
                                                                  || string.IsNullOrWhiteSpace(input.RefreshTokenSecretReference) =>
                "OAuth refresh-token authentication requires client-id, client-secret, and refresh-token references.",
            DataConnectorAuthenticationKind.PostgreSqlPassword when string.IsNullOrWhiteSpace(input.UsernameSecretReference)
                                                                    || string.IsNullOrWhiteSpace(input.PasswordSecretReference) =>
                "PostgreSQL password authentication requires username and password secret references.",
            DataConnectorAuthenticationKind.KafkaSaslPlain when string.IsNullOrWhiteSpace(input.UsernameSecretReference)
                                                                || string.IsNullOrWhiteSpace(input.PasswordSecretReference) =>
                "Kafka SASL/PLAIN authentication requires username and password secret references.",
            _ when !string.IsNullOrWhiteSpace(input.SchemaRegistryUsernameSecretReference)
                   != !string.IsNullOrWhiteSpace(input.SchemaRegistryPasswordSecretReference) =>
                "Schema Registry Basic authentication requires username and password secret references.",
            _ => null,
        };
        if (requiredError is not null)
        {
            return (null, requiredError);
        }

        return (new DataConnectorAuthentication(
            kind.Value,
            input.SecretReference,
            input.UsernameSecretReference,
            input.PasswordSecretReference,
            input.ClientIdSecretReference,
            input.ClientSecretReference,
            input.RefreshTokenSecretReference,
            input.ClientCertificateSecretReference,
            input.CertificatePasswordSecretReference,
            input.CustomHeaderName,
            input.SchemaRegistryUsernameSecretReference,
            input.SchemaRegistryPasswordSecretReference), null);
    }
}

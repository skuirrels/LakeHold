using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.OpenApi;
using Lakehold.Api;
using Lakehold.Api.Auth;
using Lakehold.Api.Cdc;
using Lakehold.Api.Connectors;
using Lakehold.Api.Endpoints;
using Lakehold.Api.Health;
using Lakehold.Api.Importing;
using Lakehold.Api.Querying;
using Lakehold.Api.Mcp;
using Lakehold.Api.PgWire;
using Lakehold.Api.PublicApi;
using Lakehold.Api.Scheduling;
using Lakehold.Api.Storage;
using Lakehold.Api.Security;
using Lakehold.ControlPlane.Data;
using Lakehold.ControlPlane.Security;
using Lakehold.Engine.Configuration;
using Lakehold.Engine.Execution;
using Lakehold.Engine.Telemetry;

// Load .env before the host is built, because the environment-variable configuration provider reads
// the process environment during CreateBuilder — set them afterwards and configuration never sees
// them. TraversePath walks up to the repository root, so this works whether the API is launched from
// its own directory, from the solution root, or by Aspire.
//
// Real environment variables win: Load does not overwrite what is already set, so a value exported
// by the shell, a container, or Aspire is never clobbered by a stale local file. Absent .env, this
// is a no-op — deployments set configuration through their platform, not through a file in source.
DotNetEnv.Env.NoClobber().TraversePath().Load();

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Lakehold's own spans and instruments. AddServiceDefaults wires the HTTP and runtime
// instrumentation, which can say a request took 400 ms but not whether that was DuckDB executing, a
// cold session attaching, or the statement queued behind another on the same tenant's gate.
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddSource(LakeholdTelemetry.ActivitySourceName))
    .WithMetrics(metrics => metrics.AddMeter(LakeholdTelemetry.MeterName));

// Readiness gains a real dependency. AddServiceDefaults registers only the "self" liveness check, so
// without this /health would report ready before the control plane was open.
builder.Services.AddHealthChecks()
    .AddCheck<ControlPlaneHealthCheck>("control-plane");

builder.Services.AddOpenApi(options =>
{
    options.AddOperationTransformer((operation, context, _) =>
    {
        var metadata = context.Description.ActionDescriptor.EndpointMetadata;
        operation.OperationId ??= PublicApiOperationIds.Create(
            context.Description.HttpMethod,
            context.Description.RelativePath);
        if (!metadata.OfType<IAllowAnonymous>().Any())
        {
            operation.Security ??= [];
            operation.Security.Add(new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference("bearerAuth")] = [],
            });
        }

        if (metadata.OfType<CursorPaginationMetadata>().SingleOrDefault() is { } pagination)
        {
            operation.Parameters ??= [];
            var defaultLimit = Math.Min(PublicApiPagination.DefaultLimit, pagination.MaximumLimit);
            var limitParameter = new OpenApiParameter
            {
                Name = "limit",
                In = ParameterLocation.Query,
                Required = false,
                Description =
                    $"Maximum items to return (1-{pagination.MaximumLimit}, default {defaultLimit}).",
                Schema = new OpenApiSchema
                {
                    Type = JsonSchemaType.Integer,
                    Format = "int32",
                    Minimum = "1",
                    Maximum = pagination.MaximumLimit.ToString(
                        System.Globalization.CultureInfo.InvariantCulture),
                    Default = defaultLimit,
                },
            };
            var existingLimit = operation.Parameters
                .Select((parameter, index) => new { parameter, index })
                .SingleOrDefault(candidate =>
                    candidate.parameter.In == ParameterLocation.Query
                    && string.Equals(candidate.parameter.Name, "limit", StringComparison.Ordinal));
            if (existingLimit is null)
            {
                operation.Parameters.Add(limitParameter);
            }
            else
            {
                operation.Parameters[existingLimit.index] = limitParameter;
            }

            if (!operation.Parameters.Any(parameter =>
                    parameter.In == ParameterLocation.Query
                    && string.Equals(parameter.Name, "cursor", StringComparison.Ordinal)))
            {
                operation.Parameters.Add(new OpenApiParameter
                {
                    Name = "cursor",
                    In = ParameterLocation.Query,
                    Required = false,
                    Description = "Opaque nextCursor from the preceding response; repeat the same query parameters.",
                    Schema = new OpenApiSchema { Type = JsonSchemaType.String },
                });
            }
        }

        if (metadata.OfType<IdempotentMutationMetadata>().Any())
        {
            operation.Parameters ??= [];
            operation.Parameters.Add(new OpenApiParameter
            {
                Name = "Idempotency-Key",
                In = ParameterLocation.Header,
                Required = false,
                Description = "A caller-generated 16-128 character key. Reuse only for an identical retry.",
                Schema = new OpenApiSchema
                {
                    Type = JsonSchemaType.String,
                    MinLength = 16,
                    MaxLength = 128,
                    Pattern = "^[!-~]{16,128}$",
                },
            });
        }

        return Task.CompletedTask;
    });
    options.AddDocumentTransformer((document, _, _) =>
    {
        document.Info.Title = "LakeHold Public API";
        document.Info.Version = "v1";
        document.Info.Description =
            "Stable, versioned control-plane and lakehouse API for LakeHold. "
            + "Send machine credentials as a Bearer token and an Idempotency-Key on retryable mutations.";
        document.Servers = [new OpenApiServer { Url = "/" }];
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>(StringComparer.Ordinal);
        document.Components.SecuritySchemes["bearerAuth"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "LakeHold API token",
            Description = "A LakeHold API token with the capability required by the operation.",
        };
        foreach (var path in document.Paths.Keys
                     .Where(path => !path.StartsWith(PublicApiRoutes.BasePath, StringComparison.Ordinal))
                     .ToArray())
        {
            document.Paths.Remove(path);
        }

        foreach (var operation in document.Paths.Values.SelectMany(path =>
                     path.Operations is null
                         ? Enumerable.Empty<OpenApiOperation>()
                         : path.Operations.Values))
        {
            if (operation.Security is not { Count: > 0 })
            {
                continue;
            }

            operation.Security =
            [
                new OpenApiSecurityRequirement
                {
                    [new OpenApiSecuritySchemeReference("bearerAuth", document)] = [],
                },
            ];
        }
        PublicApiOpenApi.NormalizeProblemResponses(document);
        foreach (var response in document.Paths.Values
                     .SelectMany(path => path.Operations is null
                         ? Enumerable.Empty<OpenApiOperation>()
                         : path.Operations.Values)
                     .SelectMany(operation => operation.Responses is null
                         ? Enumerable.Empty<IOpenApiResponse>()
                         : operation.Responses.Values))
        {
            if (response is not OpenApiResponse concreteResponse)
            {
                continue;
            }

            concreteResponse.Headers ??= new Dictionary<string, IOpenApiHeader>(StringComparer.OrdinalIgnoreCase);
            concreteResponse.Headers[PublicApiCorrelationExtensions.HeaderName] = new OpenApiHeader
            {
                Description = "Server correlation identifier for support and diagnostics.",
                Schema = new OpenApiSchema { Type = JsonSchemaType.String },
            };
        }
        PublicApiOpenApi.NormalizeNumericSchemas(document);
        PublicApiOpenApi.PruneUnusedSchemas(document);
        PublicApiOpenApi.PreserveAdditiveAccessCompatibility(document);
        PublicApiOpenApi.PreserveAdditiveConnectorCompatibility(document);
        PublicApiOpenApi.PreserveAdditiveSystemSettingsCompatibility(document);
        PublicApiOpenApi.PreserveAdditiveQueryAuditCompatibility(document);

        return Task.CompletedTask;
    });
});
// Minimal-API binding failures are handled the same way in every environment, which they are not by
// default: `ThrowOnBadRequest` defaults to true only in Development, so the same missing parameter
// produced a 500 with a stack trace on a developer's machine and a bare 400 with an empty body in
// production. Neither is the documented contract. Raising it always sends both through the handler
// below, which answers the status the framework intended as RFC 9457 problem+json.
builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
builder.Services.AddExceptionHandler<BadRequestExceptionHandler>();
builder.Services.AddProblemDetails(options =>
{
    options.CustomizeProblemDetails = context =>
        PublicApiProblems.Enrich(context.ProblemDetails, context.HttpContext);
});
builder.Services
    .AddDataProtection()
    .SetApplicationName("LakeHold")
    .PersistKeysToDbContext<ControlPlaneContext>();

builder.Services.Configure<LakehouseOptions>(builder.Configuration.GetSection(LakehouseOptions.SectionName));
builder.Services.Configure<CsvUploadOptions>(builder.Configuration.GetSection(CsvUploadOptions.SectionName));

var stateRoot = Path.GetFullPath(builder.Configuration["Lakehouse:StateRoot"] ?? "./.lakehold");
Directory.CreateDirectory(stateRoot);

// Resolve storage roots against the state root so a relative default does not follow the process's
// working directory. BackupRoot and EjectRoot are deliberately siblings of the data root: nested
// under it, both become candidates for DuckLake's own orphan cleanup and eventually delete themselves.
builder.Services.PostConfigure<LakehouseOptions>(options =>
{
    options.MetadataRoot = ResolveRoot(options.MetadataRoot, "./.lakehold/catalogs", "catalogs");
    options.DataRoot = ResolveRoot(options.DataRoot, "./.lakehold/data", "data");
    options.BackupRoot = ResolveRoot(options.BackupRoot, "./.lakehold/backups", "backups");
    options.EjectRoot = ResolveRoot(options.EjectRoot, "./.lakehold/ejects", "ejects");

    string ResolveRoot(string configured, string defaultValue, string defaultDirectory)
    {
        if (configured.Contains("://", StringComparison.Ordinal))
        {
            return configured;
        }

        if (string.Equals(configured, defaultValue, StringComparison.Ordinal))
        {
            return Path.GetFullPath(Path.Combine(stateRoot, defaultDirectory));
        }

        return Path.IsPathRooted(configured)
            ? Path.GetFullPath(configured)
            : Path.GetFullPath(Path.Combine(stateRoot, configured));
    }
});

// PostgreSQL is the durable, shared control plane. DuckDB remains the in-process query engine, but
// no production identity, catalog definition, token, schedule, or audit record is node-local.
var controlPlaneConnection = builder.Configuration.GetConnectionString("ControlPlane");
if (string.IsNullOrWhiteSpace(controlPlaneConnection))
{
    throw new InvalidOperationException(
        "ConnectionStrings:ControlPlane is required. Configure a PostgreSQL database through "
        + "ConnectionStrings__ControlPlane; Lakehold does not fall back to a node-local database.");
}

builder.Services.AddDbContext<ControlPlaneContext>(options =>
    options.UseNpgsql(
        controlPlaneConnection,
        npgsql => npgsql.MigrationsAssembly(typeof(ControlPlaneContext).Assembly.GetName().Name!)));

// One pool per node — it owns the warm compute sessions, so it must outlive any request. Catalog
// definitions themselves are re-read from shared PostgreSQL for cross-node correctness.
builder.Services.AddSingleton<DucklingPool>();
builder.Services.AddSingleton<IDucklingSessionConfigurator, DucklingSessionConfigurator>();
builder.Services.AddScoped<LakehouseService>();
builder.Services.AddScoped<SavedQueryService>();
builder.Services.AddScoped<DataConnectorService>();
builder.Services.AddScoped<PublicApiIdempotencyStore>();
builder.Services.AddScoped<PublicApiOperationStore>();
builder.Services.AddHostedService<PublicApiOperationWorker>();
builder.Services.AddHostedService<PublicApiRetentionWorker>();
builder.Services.AddSingleton<TabularScratchSpace>();
builder.Services.AddScoped<TabularUploadService>();
builder.Services.AddOptions<QueryPlannerOptions>()
    .Bind(builder.Configuration.GetSection(QueryPlannerOptions.Section))
    .Validate(
        options => options.Planners.All(planner =>
            !string.IsNullOrWhiteSpace(planner.Id)
            && planner.Endpoint.IsAbsoluteUri
            && planner.Endpoint.Scheme is "http" or "https"
            && planner.Endpoint.AbsolutePath.EndsWith('/')),
        "Every query planner needs a non-empty id and an absolute HTTP(S) base endpoint ending in '/'.")
    .Validate(
        options => options.Planners.Select(planner => planner.Id).Distinct(StringComparer.Ordinal).Count()
            == options.Planners.Count,
        "Query planner ids must be unique.")
    .Validate(
        options => options.LeavesBuiltInLanguageAlone(),
        $"No query planner may use the id '{QueryPlannerOptions.BuiltInLanguageId}'. The API plans "
        + "that language in this process and always serves it, so a planner sharing the id would "
        + "list it twice.")
    .Validate(
        options => options.MaxResponseBytes is >= 1_024 and <= 16 * 1024 * 1024,
        "Query planner responses must be capped between 1 KiB and 16 MiB.")
    .ValidateOnStart();
builder.Services.AddHttpClient(nameof(QueryPlannerRegistry), client =>
    client.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddSingleton<QueryPlannerDescriptorCache>();
builder.Services.AddScoped<QueryPlannerRegistry>();
builder.Services.AddScoped<Lakehold.Querying.IQuerySourcePlanner>(
    services => services.GetRequiredService<QueryPlannerRegistry>());
builder.Services.AddScoped<QuerySourcePlanningService>();
builder.Services.AddScoped<QueryPlanValidator>();
builder.Services.AddSingleton<QueryPlanCache>();
builder.Services.AddScoped<QueryExecutionCoordinator>();

// Authentication: resolve a bearer token or browser identity to a principal, then validate the
// route against it in the endpoint filter. Credential-less requests exist only through an explicitly
// configured, catalog-scoped demo reader; a presented credential is always validated and cannot
// fall through to that identity.
builder.Services.Configure<LakeholdAuthOptions>(builder.Configuration.GetSection(LakeholdAuthOptions.Section));
builder.Services.TryAddSingleton(TimeProvider.System);
builder.Services.AddScoped<ApiTokenAuthenticator>();
builder.Services.AddScoped<MemberDirectory>();

// OIDC for humans, tokens for machines, one principal behind both. Configuring an authority is what
// turns this on: absent one the whole path stays off, so an air-gapped install never acquires a
// dependency on an identity provider it cannot reach. See docs/AUTHENTICATION.md.
builder.Services.Configure<LakeholdOidcOptions>(builder.Configuration.GetSection(LakeholdOidcOptions.Section));
var oidc = builder.Configuration.GetSection(LakeholdOidcOptions.Section).Get<LakeholdOidcOptions>()
    ?? new LakeholdOidcOptions();
oidc.ValidateForStartup();
builder.Services.AddLakeholdAuthentication(oidc);

// Creating users in the provider, which only built-in identity mode does. The service is registered
// either way so the endpoint can answer "not on this node" rather than 500 on a missing dependency;
// it reports itself unavailable under SSO and refuses before touching the network.
builder.Services.AddHttpClient(KeycloakUserProvisioner.HttpClientName);
builder.Services.AddSingleton<IUserProvisioner, KeycloakUserProvisioner>();

// Scheduled flush/backup/compact. A backup that depends on someone pressing a button is not a
// recovery guarantee; unflushed inlined data is permanently unrecoverable, so both must be automatic.
builder.AddMaintenanceScheduling();

// Outbound CDC: polls subscribed catalogs for new snapshots and posts signed change payloads.
// DuckLake already records what every snapshot changed, so this reads existing bookkeeping rather
// than adding a Debezium/Kafka pipeline beside the lakehouse.
builder.Services
    .AddOptions<CdcOptions>()
    .Bind(builder.Configuration.GetSection(CdcOptions.SectionName))
    .Validate(
        settings => settings.PollInterval > TimeSpan.Zero
                    && settings.MaxChangesPerTable is > 0 and <= 10_000
                    && settings.MaxSnapshotsPerSubscriptionPerSweep > 0
                    && settings.MaxConcurrentSubscriptions > 0
                    && settings.DeliveryTimeout > TimeSpan.Zero
                    && settings.LeaseDuration > settings.DeliveryTimeout
                    && settings.MaxBackoff >= settings.PollInterval,
        "CDC intervals and concurrency limits are invalid; LeaseDuration must exceed DeliveryTimeout.")
    .ValidateOnStart();
builder.Services
    .AddHttpClient(ChangeFeedDispatcher.HttpClientName)
    .ConfigurePrimaryHttpMessageHandler(WebhookConnection.CreateHandler);
if (builder.Configuration.GetSection(CdcOptions.SectionName).Get<CdcOptions>()?.Enabled ?? true)
{
    builder.Services.AddHostedService<ChangeFeedDispatcher>();
}

// Managed full-snapshot and checkpointed incremental ingestion. Definitions, schedules, leases,
// lineage, and outcomes are durable in PostgreSQL; response bytes use disposable node-local scratch
// only until DuckLake has committed the replacement or keyed upsert.
builder.Services
    .AddOptions<ConnectorOptions>()
    .Bind(builder.Configuration.GetSection(ConnectorOptions.SectionName))
    .Validate(
        settings => settings.PollInterval > TimeSpan.Zero
                    && settings.LeaseDuration > settings.RequestTimeout
                    && settings.RequestTimeout > TimeSpan.Zero
                    && settings.MaxConcurrentRuns is > 0 and <= 32
                    && settings.MaxSnapshotBytes > 0
                    && settings.MaxRows > 0
                    && settings.MaxPaginationPages is > 0 and <= 100_000
                    && settings.MaxHubSpotResultsPerWindow is > 0 and <= 9_000
                    && settings.HubSpotIndexingDelay >= TimeSpan.Zero
                    && settings.HubSpotCheckpointOverlap >= settings.HubSpotIndexingDelay
                    && settings.HubSpotMinimumRequestInterval >= TimeSpan.FromMilliseconds(200)
                    && settings.MaxRecordBytes > 0
                    && settings.MaxRecordBytes <= settings.MaxSnapshotBytes
                    && settings.MaxRecordBytes <= int.MaxValue - (64 * 1024)
                    && settings.MaxAggregateScratchBytes >= settings.MaxSnapshotBytes
                    && settings.MinimumFreeBytes >= 0
                    && settings.StaleFileAge > TimeSpan.Zero
                    && (string.IsNullOrWhiteSpace(settings.SecretProviderEndpoint)
                        || Uri.TryCreate(settings.SecretProviderEndpoint, UriKind.Absolute, out var secretEndpoint)
                        && secretEndpoint.Scheme == Uri.UriSchemeHttps
                        && string.IsNullOrEmpty(secretEndpoint.UserInfo))
                    && (string.IsNullOrWhiteSpace(settings.SecretProviderTokenEnvironmentVariable)
                        || settings.SecretProviderTokenEnvironmentVariable.All(character =>
                            char.IsAsciiLetterOrDigit(character) || character == '_'))
                    && settings.SecretBindings.All(binding =>
                        !string.IsNullOrWhiteSpace(binding.TenantSlug)
                        && !string.IsNullOrWhiteSpace(binding.CatalogName)
                        && !string.IsNullOrWhiteSpace(binding.Reference)
                        && !string.IsNullOrWhiteSpace(binding.DestinationHost)
                        && (binding.Reference.StartsWith("env://", StringComparison.OrdinalIgnoreCase)
                            || binding.Reference.StartsWith("vault://", StringComparison.OrdinalIgnoreCase))
                        && !binding.Reference.EndsWith("://", StringComparison.Ordinal)
                        && Uri.CheckHostName(binding.DestinationHost) != UriHostNameType.Unknown)
                    && settings.SecretBindings
                        .Select(binding => string.Join(
                            '\n',
                            binding.TenantSlug.ToUpperInvariant(),
                            binding.CatalogName.ToUpperInvariant(),
                            binding.Reference,
                            binding.DestinationHost.ToUpperInvariant()))
                        .Distinct(StringComparer.Ordinal)
                        .Count() == settings.SecretBindings.Length,
        "Connector limits are invalid; LeaseDuration must exceed RequestTimeout.")
    .ValidateOnStart();
builder.Services
    .AddHttpClient(RestDataConnectorSource.HttpClientName)
    .ConfigureHttpClient(client => client.Timeout = Timeout.InfiniteTimeSpan)
    .ConfigurePrimaryHttpMessageHandler(OutboundConnection.CreateHandler);
builder.Services
    .AddHttpClient(VaultConnectorSecretProvider.HttpClientName)
    .ConfigureHttpClient(client => client.Timeout = Timeout.InfiniteTimeSpan)
    .ConfigurePrimaryHttpMessageHandler(OutboundConnection.CreateHandler);
builder.Services
    .AddHttpClient(HubSpotContactsDataConnectorSource.HttpClientName)
    .ConfigureHttpClient(client => client.Timeout = Timeout.InfiniteTimeSpan)
    .ConfigurePrimaryHttpMessageHandler(OutboundConnection.CreateHandler);
builder.Services.AddSingleton<ConnectorScratchSpace>();
builder.Services.AddScoped<IConnectorSecretProvider, EnvironmentConnectorSecretProvider>();
builder.Services.AddScoped<IConnectorSecretProvider, VaultConnectorSecretProvider>();
builder.Services.AddScoped<ConnectorSecretResolver>();
builder.Services.AddScoped<IDataConnectorSource, RestDataConnectorSource>();
builder.Services.AddScoped<IGrpcConnectorTransport, GrpcConnectorTransport>();
builder.Services.AddScoped<IDataConnectorSource, GrpcDataConnectorSource>();
builder.Services.AddScoped<IDataConnectorSource, PostgreSqlDataConnectorSource>();
builder.Services.AddSingleton<HubSpotRequestLimiter>();
builder.Services.AddScoped<IDataConnectorSource, HubSpotContactsDataConnectorSource>();
builder.Services.AddScoped<IDataConnectorSource, KafkaAvroDataConnectorSource>();
builder.Services.AddScoped<DataConnectorSourceResolver>();
builder.Services.AddScoped<ConnectorRunner>();
builder.Services.AddScoped<ConnectorExecutionService>();
if (builder.Configuration.GetSection(ConnectorOptions.SectionName).Get<ConnectorOptions>()?.Enabled ?? true)
{
    builder.Services.AddHostedService<ConnectorWorker>();
}

// PostgreSQL wire endpoint: lets Power BI, Tableau, Metabase, and psql connect to a tenant catalog
// with no connector to install, because they already speak this protocol. See docs/POSTGRES-WIRE.md.
builder.Services.Configure<PgWireOptions>(builder.Configuration.GetSection(PgWireOptions.SectionName));
var pgWire = builder.Configuration.GetSection(PgWireOptions.SectionName).Get<PgWireOptions>() ?? new PgWireOptions();
if (pgWire.Enabled)
{
    // Fail closed. This opens a database port onto every catalog the node serves, so starting it
    // without a password has to be an explicit decision rather than the consequence of an
    // unset configuration key.
    if (pgWire.Password.Length == 0 && pgWire.TenantPasswords.Count == 0
        && !pgWire.AllowAnonymous && !pgWire.AllowTokenAuthentication)
    {
        throw new InvalidOperationException(
            "Lakehold:PgWire is enabled but no credentials are configured. Set per-tenant passwords "
            + "(Lakehold__PgWire__TenantPasswords__<tenant>) in .env, or Lakehold__PgWire__Password "
            + "for a single shared credential, or set Lakehold:PgWire:AllowTokenAuthentication to "
            + "accept API tokens, or set Lakehold:PgWire:AllowAnonymous to true to accept "
            + "unauthenticated connections deliberately.");
    }

    // Token authentication has to ask for the password in the clear, because the token store holds
    // only a hash. Refusing to start is the right failure: the alternative is a credential that
    // authenticates every Lakehold surface crossing an unencrypted socket.
    if (pgWire.AllowTokenAuthentication && !pgWire.RequireTls && !pgWire.AllowCleartextPassword)
    {
        throw new InvalidOperationException(
            "Lakehold:PgWire:AllowTokenAuthentication requires the password in the clear, so it must "
            + "run under TLS. Set Lakehold:PgWire:RequireTls (with a certificate), or set "
            + "Lakehold:PgWire:AllowCleartextPassword to accept the risk on a trusted network.");
    }

    if (pgWire.RequireTls && pgWire.TlsCertificatePath.Length == 0)
    {
        // Refusing every connection at run time is a worse way to learn this than refusing to start.
        throw new InvalidOperationException(
            "Lakehold:PgWire:RequireTls is set but no certificate is configured. Set "
            + "Lakehold:PgWire:TlsCertificatePath, or clear RequireTls to serve plaintext.");
    }

    builder.Services.AddHostedService<PgWireServer>();
}

// Model Context Protocol: lets an AI agent explore a catalog and run read-only SQL under a
// credential that already means something. Off unless enabled, and it always demands a real
// credential — the demo reader that publishes a catalog anonymously is not enough. See docs/MCP.md.
builder.AddLakeholdMcp();

// The Angular dev server is a separate origin; the browser will not call the API without this.
const string DevCors = "lakehold-dev";
builder.Services.AddCors(options => options.AddPolicy(DevCors, policy => policy
    .WithOrigins("http://localhost:5399", "https://localhost:5399")
    .AllowAnyHeader()
    .AllowAnyMethod()));

var app = builder.Build();

// Fail fast on invalid scratch limits and scavenge files abandoned by an earlier process before
// this node can accept a CSV or XLSX upload.
_ = app.Services.GetRequiredService<TabularScratchSpace>();
_ = app.Services.GetRequiredService<ConnectorScratchSpace>();

app.MapDefaultEndpoints();

// The compatibility rewrite must run before routing. The old /api path remains available for one
// release, but every response points clients at the canonical, documented v1 contract.
app.UseLegacyApiCompatibility();
app.UsePublicApiCorrelation();
app.UseRouting();
app.UsePublicApiRequestHashing();

if (app.Environment.IsDevelopment())
{
    app.UseCors(DevCors);
}

app.UseExceptionHandler();

// Only when an authority is configured: without a registered scheme these throw, and the endpoint
// filter is what enforces access in either case — this middleware only populates HttpContext.User.
if (oidc.Enabled)
{
    app.UseAuthentication();
    app.UseAuthorization();
}

var publicApi = app.MapGroup(PublicApiRoutes.BasePath)
    .AddEndpointFilter<PublicApiProblemFilter>();
foreach (var status in new[]
         {
             StatusCodes.Status400BadRequest,
             StatusCodes.Status401Unauthorized,
             StatusCodes.Status403Forbidden,
             StatusCodes.Status404NotFound,
             StatusCodes.Status408RequestTimeout,
             StatusCodes.Status409Conflict,
             StatusCodes.Status412PreconditionFailed,
             StatusCodes.Status413PayloadTooLarge,
             StatusCodes.Status422UnprocessableEntity,
             StatusCodes.Status429TooManyRequests,
             StatusCodes.Status500InternalServerError,
             StatusCodes.Status502BadGateway,
             StatusCodes.Status503ServiceUnavailable,
             StatusCodes.Status504GatewayTimeout,
         })
{
    ((IEndpointConventionBuilder)publicApi).Add(endpoint => endpoint.Metadata.Add(new ProducesResponseTypeMetadata(
        status,
        typeof(PublicApiProblemDetails),
        ["application/problem+json"])));
}
publicApi.MapPublicApiDiscovery();
publicApi.MapPublicApiOperations();
publicApi.MapLakehouseEndpoints();
publicApi.MapSystemSettingsEndpoints();
app.MapOpenApi(PublicApiRoutes.BasePath + "/openapi/{documentName}.json");
app.MapGet(PublicApiRoutes.OpenApiPath, () => Results.Redirect(
        PublicApiRoutes.BasePath + "/openapi/v1.json",
        permanent: false,
        preserveMethod: true))
    .AllowAnonymous()
    .ExcludeFromDescription();
app.MapBrowserAuthenticationEndpoints();
app.MapLakeholdMcp();
app.MapMcpResourceMetadata();

app.LogMaintenanceSchedule();

// Migrations always run; the demo catalog only where it was asked for. Defaulting to
// the environment rather than to true means a production image seeds nothing unless told to, and a
// developer's compose stack is still self-demonstrating on first run.
var seedDemoData = builder.Configuration.GetValue("Lakehold:SeedDemoData", app.Environment.IsDevelopment());
await ControlPlaneDatabase.MigrateAsync(app.Services, app.Logger).ConfigureAwait(false);
await DemoData.EnsureSeededAsync(app.Services, stateRoot, app.Logger, seedDemoData).ConfigureAwait(false);

// Bootstrap the first credential once the schema exists. On a node with no tokens this mints an
// instance-scoped one and logs it once, so a fresh production deployment can be provisioned at all.
// Lakehold__BootstrapToken overrides it for platforms that inject credentials externally.
await TokenBootstrap.EnsureBootstrapTokenAsync(
    app.Services,
    builder.Configuration["Lakehold:BootstrapToken"],
    app.Logger,
    TimeProvider.System).ConfigureAwait(false);

await app.RunAsync().ConfigureAwait(false);

using Lakehold.Api.Auth;
using Lakehold.Api.Mcp;
using Lakehold.Api.Storage;
using Lakehold.ControlPlane.Security;
using Lakehold.Engine.Catalog;
using Lakehold.Engine.Configuration;
using Microsoft.Extensions.Options;

namespace Lakehold.Api.Endpoints;

/// <summary>Instance-level operator settings.</summary>
public static class SystemSettingsEndpoints
{
    /// <summary>Maps the settings API, accessible only to an instance credential.</summary>
    public static IEndpointRouteBuilder MapSystemSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var settings = app.MapGroup("/system-settings")
            .WithTags("System settings")
            .AddEndpointFilter<LakeholdAuthorizationFilter>()
            .RequireCapability(Capability.Instance);

        settings.MapGet("/", GetAsync)
            .Produces<SystemSettingsDto>()
            .WithSummary("Returns the instance-wide runtime settings.");

        settings.MapPut("/", SaveAsync)
            .Produces<SystemSettingsDto>()
            .WithSummary("Saves and immediately applies the instance-wide runtime settings.");

        // Deliberately inside this group rather than a `/system` sibling: the group already declares
        // the authorization this route needs, and a second spelling for one instance-operator surface
        // would be two places to keep a capability correct.
        settings.MapGet("/storage", GetStorage)
            .Produces<SystemStorageDto>()
            .WithSummary("Returns this node's Parquet storage placement and redacted profile inventory.");

        // A POST that creates nothing. It reads like a query, but the inputs are a small object
        // rather than a scalar, and a tenant slug and catalog name do not belong in a URL that ends
        // up in access logs and browser history.
        settings.MapPost("/storage/resolve", ResolveStoragePath)
            .Produces<ResolvedStoragePathDto>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .WithSummary("Resolves where a catalog's Parquet would go, without creating anything.");

        return app;
    }

    private static async Task<IResult> GetAsync(
        McpRuntimeSettingsStore store,
        CancellationToken cancellationToken)
    {
        var settings = await store.GetAsync(cancellationToken).ConfigureAwait(false);
        return Results.Ok(ToDto(settings));
    }

    private static async Task<IResult> SaveAsync(
        UpdateSystemSettingsRequest request,
        McpRuntimeSettingsStore store,
        CancellationToken cancellationToken)
    {
        try
        {
            var settings = await store.SaveAsync(
                    request.McpEnabled,
                    request.McpAllowWrites,
                    request.McpMaxRowsPerResult,
                    request.McpPublicBaseUrl,
                    request.Version,
                    cancellationToken)
                .ConfigureAwait(false);
            return Results.Ok(ToDto(settings));
        }
        catch (SystemSettingsValidationException ex)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid system settings",
                detail: ex.Message);
        }
        catch (SystemSettingsConflictException ex)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "System settings changed",
                detail: ex.Message);
        }
    }

    /// <summary>
    ///     Projects the deployment's storage configuration into a shape with no credential-bearing
    ///     member on it. Synchronous and allocation-cheap: the options are already bound in memory,
    ///     and nothing here reaches a catalog, a bucket, or the control plane.
    /// </summary>
    internal static IResult GetStorage(IOptions<LakehouseOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var lakehouse = options.Value;

        return Results.Ok(new SystemStorageDto(
            lakehouse.DataRoot,
            lakehouse.BackupRoot,
            lakehouse.EjectRoot,
            NullIfBlank(lakehouse.DefaultStorageProfile),
            [.. lakehouse.StorageProfiles
                .OrderBy(profile => profile.Key, StringComparer.OrdinalIgnoreCase)
                .Select(profile => ToSummary(profile.Key, profile.Value))],
            RequiresRestartToChange: true));
    }

    /// <summary>
    ///     Previews a placement using the same rules catalog creation applies, so the browser never
    ///     joins a URI or decides whether a profile matches a scheme.
    /// </summary>
    /// <remarks>
    ///     Non-mutating by construction rather than by care: <c>CatalogPlacement</c> reads only the
    ///     bound options, so there is no directory, object, metadata schema, or catalog row this
    ///     could create even if it were called in a loop. The duplicate-name and duplicate-path
    ///     conflicts are deliberately not checked here — they are create-time conflicts that only
    ///     the write can settle, and the write still does.
    /// </remarks>
    internal static IResult ResolveStoragePath(
        ResolveStoragePathRequest request,
        IOptions<LakehouseOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (request is null
            || string.IsNullOrWhiteSpace(request.TenantSlug)
            || string.IsNullOrWhiteSpace(request.CatalogName))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid placement request",
                detail: "A tenant slug and catalog name are required to resolve a data path.");
        }

        if (!CatalogPlacement.TryResolve(
                options.Value,
                request.TenantSlug,
                request.CatalogName,
                request.DataPath,
                request.StorageProfile,
                out var placement,
                out var error))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid storage placement",
                detail: error);
        }

        return Results.Ok(new ResolvedStoragePathDto(
            placement.DataPath,
            placement.Kind.ToString(),
            placement.StorageProfile,
            placement.Derived));
    }

    private static StorageProfileSummaryDto ToSummary(string name, ParquetStorageProfileOptions profile) =>
        new(
            name,
            profile.Kind.ToString(),
            NullIfBlank(profile.Region),
            EndpointHost(profile.Endpoint),
            profile.UseSsl,
            profile.UrlStyle,
            HasRequiredCredentials(profile),
            AzureAuthentication(profile));

    /// <summary>
    ///     Answers the question the operator is actually asking — "will this profile attach?" — by
    ///     mirroring the settings <c>DucklingSessionConfigurator</c> requires before it will create the
    ///     secret. Anything looser would report a profile as ready that fails at the first query.
    /// </summary>
    private static bool HasRequiredCredentials(ParquetStorageProfileOptions profile) => profile.Kind switch
    {
        // A local path creates no secret at all, so there is nothing that could be missing.
        ParquetStorageKind.Local => true,
        ParquetStorageKind.Azure => !string.IsNullOrWhiteSpace(profile.AzureConnectionString)
            || !string.IsNullOrWhiteSpace(profile.AzureAccountName),
        _ => !string.IsNullOrWhiteSpace(profile.KeyId) && !string.IsNullOrWhiteSpace(profile.Secret),
    };

    /// <summary>
    ///     Which Azure mode this profile would use, in the same order the secret builder resolves it:
    ///     a connection string wins, an account name is the credential-chain path, and neither is a
    ///     profile with nothing to authenticate with rather than a mode.
    /// </summary>
    private static string? AzureAuthentication(ParquetStorageProfileOptions profile)
    {
        if (profile.Kind != ParquetStorageKind.Azure)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(profile.AzureConnectionString))
        {
            return "connection-string";
        }

        return string.IsNullOrWhiteSpace(profile.AzureAccountName) ? null : "credential-chain";
    }

    /// <summary>
    ///     Strips any userinfo from a configured endpoint. DuckDB's <c>ENDPOINT</c> takes a bare
    ///     <c>host[:port]</c>, so this normally changes nothing — but a deployment that wrote
    ///     <c>key:secret@host</c> there anyway must not have it handed back by a redacted response.
    /// </summary>
    private static string? EndpointHost(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return null;
        }

        var separator = endpoint.LastIndexOf('@');
        return NullIfBlank(separator < 0 ? endpoint : endpoint[(separator + 1)..]);
    }

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static SystemSettingsDto ToDto(McpRuntimeSettings settings) =>
        new(
            settings.Enabled,
            settings.AllowWrites,
            settings.MaxRowsPerResult,
            settings.PublicBaseUrl,
            settings.Route,
            settings.Version,
            settings.UpdatedUtc);
}

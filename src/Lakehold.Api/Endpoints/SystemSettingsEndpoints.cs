using Lakehold.Api.Auth;
using Lakehold.Api.Mcp;
using Lakehold.ControlPlane.Security;

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

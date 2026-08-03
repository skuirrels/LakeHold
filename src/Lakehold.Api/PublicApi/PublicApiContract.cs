using System.Text.RegularExpressions;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;

namespace Lakehold.Api.PublicApi;

/// <summary>Stable route and compatibility constants for LakeHold's public HTTP contract.</summary>
public static class PublicApiRoutes
{
    public const string BasePath = "/api/v1";
    public const string LegacyBasePath = "/api";
    public const string OpenApiPath = BasePath + "/openapi.json";
    public const string Sunset = "Sun, 01 Nov 2026 00:00:00 GMT";

    public static string Canonical(string relativePath)
        => string.Concat(BasePath, relativePath.StartsWith('/') ? relativePath : "/" + relativePath);
}

/// <summary>Machine-readable server capabilities used by SDKs for feature negotiation.</summary>
public sealed record PublicApiCapabilities(
    string Product,
    string ApiVersion,
    string MinimumSdkApiVersion,
    IReadOnlyList<string> Features,
    PublicApiLinks Links);

/// <summary>Stable public discovery links.</summary>
public sealed record PublicApiLinks(string OpenApi);

/// <summary>RFC 9457 details plus LakeHold's stable machine and correlation fields.</summary>
public sealed class PublicApiProblemDetails : ProblemDetails
{
    [JsonPropertyName("code")]
    public required string Code { get; init; }

    [JsonPropertyName("requestId")]
    public required string RequestId { get; init; }
}

/// <summary>Maps public API discovery endpoints.</summary>
public static class PublicApiEndpointExtensions
{
    private static readonly string[] Features =
    [
        "audit-history",
        "backups",
        "catalogs",
        "cdc",
        "connectors",
        "cursor-pagination",
        "durable-operations",
        "eject",
        "idempotency",
        "maintenance",
        "openapi",
        "query",
        "query-languages",
        "saved-queries",
        "snapshots",
        "system-settings",
    ];

    public static RouteGroupBuilder MapPublicApiDiscovery(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        api.MapGet("/capabilities", () => TypedResults.Ok(new PublicApiCapabilities(
                "LakeHold",
                "v1",
                "v1",
                Features,
                new PublicApiLinks(PublicApiRoutes.OpenApiPath))))
            .AllowAnonymous()
            .WithTags("Discovery")
            .WithName("GetApiCapabilities")
            .WithSummary("Returns the public API version and optional capabilities supported by this server.");

        return api;
    }
}

/// <summary>Adds the temporary unversioned-to-v1 compatibility path before endpoint routing.</summary>
public static class LegacyApiCompatibilityExtensions
{
    private const string LegacyRequestItem = "lakehold.public-api.legacy-request";

    public static IApplicationBuilder UseLegacyApiCompatibility(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        return app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments(
                    PublicApiRoutes.LegacyBasePath,
                    out var remaining)
                && !context.Request.Path.StartsWithSegments(PublicApiRoutes.BasePath))
            {
                context.Items[LegacyRequestItem] = true;
                context.Request.Path = new PathString(PublicApiRoutes.BasePath).Add(remaining);
                context.Response.Headers["Deprecation"] = "true";
                context.Response.Headers["Sunset"] = PublicApiRoutes.Sunset;
                context.Response.Headers.Link =
                    $"<{PublicApiRoutes.OpenApiPath}>; rel=\"deprecation\"; type=\"application/vnd.oai.openapi+json\"";
            }

            await next(context).ConfigureAwait(false);
        });
    }

    public static bool IsLegacyApiRequest(this HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Items.ContainsKey(LegacyRequestItem);
    }
}

/// <summary>Publishes the server correlation identifier on every versioned API response.</summary>
public static class PublicApiCorrelationExtensions
{
    public const string HeaderName = "X-Request-Id";

    public static IApplicationBuilder UsePublicApiCorrelation(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        return app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments(PublicApiRoutes.BasePath))
            {
                context.Response.OnStarting(() =>
                {
                    context.Response.Headers[HeaderName] = context.TraceIdentifier;
                    return Task.CompletedTask;
                });
            }

            await next(context).ConfigureAwait(false);
        });
    }
}

/// <summary>Normalizes endpoint-filter results into LakeHold's RFC 9457 public error contract.</summary>
public sealed class PublicApiProblemFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        var result = await next(context).ConfigureAwait(false);
        return PublicApiProblems.Normalize(result, context.HttpContext);
    }
}

/// <summary>Creates stable, bounded RFC 9457 responses for endpoint and exception failures.</summary>
public static class PublicApiProblems
{
    private const int MaxDetailLength = 2_048;

    public static object? Normalize(object? result, HttpContext context)
    {
        if (result is not IStatusCodeHttpResult statusResult
            || statusResult.StatusCode is not >= StatusCodes.Status400BadRequest)
        {
            return result;
        }

        if (result is IValueHttpResult { Value: ProblemDetails problem })
        {
            Enrich(problem, context);
            return result;
        }

        var status = statusResult.StatusCode.Value;
        var detail = result is IValueHttpResult { Value: string text }
            ? BoundedDetail(text)
            : ReasonPhrases.GetReasonPhrase(status);
        return Create(context, status, detail);
    }

    public static IResult Create(HttpContext context, int status, string? detail = null, string? code = null)
    {
        var problem = new PublicApiProblemDetails
        {
            Type = "about:blank",
            Title = ReasonPhrases.GetReasonPhrase(status),
            Status = status,
            Detail = BoundedDetail(detail ?? ReasonPhrases.GetReasonPhrase(status)),
            Instance = context.Request.Path,
            Code = code ?? CodeFor(status),
            RequestId = context.TraceIdentifier,
        };
        return Results.Json(
            problem,
            statusCode: status,
            contentType: "application/problem+json");
    }

    public static void Enrich(ProblemDetails problem, HttpContext context)
    {
        var status = problem.Status ?? StatusCodes.Status500InternalServerError;
        problem.Status = status;
        problem.Title ??= ReasonPhrases.GetReasonPhrase(status);
        problem.Instance ??= context.Request.Path;
        problem.Detail = BoundedDetail(problem.Detail ?? ReasonPhrases.GetReasonPhrase(status));
        if (problem is not PublicApiProblemDetails)
        {
            problem.Extensions.TryAdd("code", CodeFor(status));
            problem.Extensions.TryAdd("requestId", context.TraceIdentifier);
        }
    }

    public static string BoundedDetail(string detail)
    {
        var normalized = detail.ReplaceLineEndings(" ").Trim();
        return normalized.Length <= MaxDetailLength
            ? normalized
            : string.Concat(normalized.AsSpan(0, MaxDetailLength - 1), "…");
    }

    internal static string CodeFor(int status) => status switch
    {
        StatusCodes.Status400BadRequest => "invalid_request",
        StatusCodes.Status401Unauthorized => "unauthorized",
        StatusCodes.Status403Forbidden => "forbidden",
        StatusCodes.Status404NotFound => "not_found",
        StatusCodes.Status408RequestTimeout => "request_timeout",
        StatusCodes.Status409Conflict => "conflict",
        StatusCodes.Status412PreconditionFailed => "precondition_failed",
        StatusCodes.Status413PayloadTooLarge => "payload_too_large",
        StatusCodes.Status422UnprocessableEntity => "unprocessable_entity",
        StatusCodes.Status429TooManyRequests => "rate_limited",
        StatusCodes.Status502BadGateway => "upstream_failure",
        StatusCodes.Status503ServiceUnavailable => "unavailable",
        StatusCodes.Status504GatewayTimeout => "upstream_timeout",
        _ when status >= StatusCodes.Status500InternalServerError => "internal_error",
        _ => "request_failed",
    };
}

/// <summary>Builds deterministic operation identifiers when an endpoint has no explicit name.</summary>
public static partial class PublicApiOperationIds
{
    [GeneratedRegex("[^A-Za-z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex Separators();

    public static string Create(string? method, string? relativePath)
    {
        var value = string.Concat(method?.ToLowerInvariant() ?? "operation", "_", relativePath ?? "root");
        return Separators().Replace(value, "_").Trim('_');
    }
}

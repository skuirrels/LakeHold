using System.Collections;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Metadata;

namespace Lakehold.Api.PublicApi;

/// <summary>A stable, cursor-based page returned by canonical public list endpoints.</summary>
public sealed record CursorPage<T>(IReadOnlyList<T> Items, string? NextCursor);

/// <summary>Pagination metadata and filtering for bounded public list endpoints.</summary>
public static class PublicApiPagination
{
    private const string RequestStateItem = "lakehold.public-api.pagination";
    public const int DefaultLimit = 100;
    public const int MaximumLimit = 500;

    /// <summary>
    /// Adds the canonical cursor envelope to a list endpoint. Legacy <c>/api</c> requests retain the
    /// original array response during the compatibility window.
    /// </summary>
    public static RouteHandlerBuilder WithCursorPagination<T>(
        this RouteHandlerBuilder builder,
        int maximumLimit = MaximumLimit)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumLimit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumLimit, MaximumLimit);

        return builder
            .AddEndpointFilter<CursorPaginationFilter<T>>()
            .Produces<CursorPage<T>>(StatusCodes.Status200OK)
            .WithMetadata(new CursorPaginationMetadata(typeof(T), maximumLimit));
    }

    /// <summary>
    /// Applies the decoded cursor window to an ordered database query. The extra row is used only
    /// to decide whether a next cursor exists and is never returned to the caller.
    /// </summary>
    public static IQueryable<T> ApplySourceWindow<T>(IQueryable<T> query, HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(query);
        var state = GetRequestState(http);
        if (state is null)
        {
            return query;
        }

        state.SourceWindowApplied = true;
        return query.Skip(state.Offset).Take(state.Limit + 1);
    }

    /// <summary>Returns the number of leading ordered items needed by a source without skip support.</summary>
    public static int RequiredSourceCount(HttpContext http)
    {
        var state = GetRequestState(http);
        return state is null
            ? MaximumLimit
            : checked(state.Offset + state.Limit + 1);
    }

    private static PaginationRequestState? GetRequestState(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        return http.Items.TryGetValue(RequestStateItem, out var value)
            ? value as PaginationRequestState
            : null;
    }

    internal static void SetRequestState(HttpContext http, PaginationRequestState state)
        => http.Items[RequestStateItem] = state;
}

internal sealed record CursorPaginationMetadata(Type ItemType, int MaximumLimit);

internal sealed class PaginationRequestState(int offset, int limit)
{
    public int Offset { get; } = offset;

    public int Limit { get; } = limit;

    public bool SourceWindowApplied { get; set; }
}

internal sealed class CursorPaginationFilter<T>(IDataProtectionProvider protectionProvider) : IEndpointFilter
{
    private const string Purpose = "LakeHold.PublicApi.Cursor.v1";
    internal static readonly TimeSpan CursorLifetime = TimeSpan.FromHours(24);
    private readonly ITimeLimitedDataProtector _protector = protectionProvider
        .CreateProtector(Purpose)
        .ToTimeLimitedDataProtector();

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (http.IsLegacyApiRequest())
        {
            return await next(context).ConfigureAwait(false);
        }

        var maximumLimit = http.GetEndpoint()?.Metadata.GetMetadata<CursorPaginationMetadata>()?.MaximumLimit
            ?? PublicApiPagination.MaximumLimit;
        if (!TryReadLimit(http, maximumLimit, out var limit))
        {
            return PublicApiProblems.Create(
                http,
                StatusCodes.Status400BadRequest,
                detail: $"The limit query parameter must be an integer from 1 to {maximumLimit}.",
                code: "invalid_page_limit");
        }

        var scope = CreateScope(http.Request);
        if (!TryReadOffset(http, scope, limit, out var offset))
        {
            return PublicApiProblems.Create(
                http,
                StatusCodes.Status400BadRequest,
                detail: "The cursor is invalid, expired, or belongs to a different request.",
                code: "invalid_cursor");
        }

        var state = new PaginationRequestState(offset, limit);
        PublicApiPagination.SetRequestState(http, state);
        var result = await next(context).ConfigureAwait(false);
        if (result is not IValueHttpResult { Value: { } value }
            || !TryMaterialize(value, out var items))
        {
            return result;
        }

        if (!state.SourceWindowApplied && offset > items.Count)
        {
            return PublicApiProblems.Create(
                http,
                StatusCodes.Status400BadRequest,
                detail: "The cursor is outside the available result set.",
                code: "invalid_cursor");
        }

        var page = (state.SourceWindowApplied ? items : items.Skip(offset)).Take(limit).ToArray();
        var nextOffset = state.Offset + page.Length;
        var hasMore = state.SourceWindowApplied
            ? items.Count > limit
            : nextOffset < items.Count;
        var nextCursor = hasMore
            ? _protector.Protect(
                JsonSerializer.Serialize(new CursorPayload(scope, nextOffset)),
                CursorLifetime)
            : null;

        return Results.Ok(new CursorPage<T>(page, nextCursor));
    }

    private static bool TryMaterialize(object value, out IReadOnlyList<T> items)
    {
        if (value is IEnumerable<T> typed)
        {
            items = typed.ToArray();
            return true;
        }

        if (value is IEnumerable untyped)
        {
            var materialized = untyped.Cast<object?>().ToArray();
            if (materialized.All(item => item is T))
            {
                items = materialized.Cast<T>().ToArray();
                return true;
            }
        }

        items = [];
        return false;
    }

    private static bool TryReadLimit(HttpContext http, int maximumLimit, out int limit)
    {
        var raw = http.Request.Query["limit"].ToString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            limit = Math.Min(PublicApiPagination.DefaultLimit, maximumLimit);
            return true;
        }

        return int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out limit)
               && limit >= 1
               && limit <= maximumLimit;
    }

    private bool TryReadOffset(HttpContext http, string scope, int limit, out int offset)
    {
        offset = 0;
        var cursor = http.Request.Query["cursor"].ToString();
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return true;
        }

        try
        {
            var payload = JsonSerializer.Deserialize<CursorPayload>(_protector.Unprotect(cursor));
            if (payload is null
                || payload.Scope is null
                || payload.Offset < 0
                || payload.Offset > int.MaxValue - limit - 1
                || !CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(payload.Scope),
                    Encoding.UTF8.GetBytes(scope)))
            {
                return false;
            }

            offset = payload.Offset;
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string CreateScope(HttpRequest request)
    {
        var query = request.Query
            .Where(pair => !string.Equals(pair.Key, "cursor", StringComparison.OrdinalIgnoreCase))
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .SelectMany(pair => pair.Value
                .OrderBy(value => value, StringComparer.Ordinal)
                .Select(value => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(value ?? string.Empty)}"));
        var canonical = $"{request.Path}?{string.Join('&', query)}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private sealed record CursorPayload(string Scope, int Offset);
}

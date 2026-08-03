using System.Buffers.Binary;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Lakehold.Api.Auth;
using Lakehold.ControlPlane.Data;
using Lakehold.ControlPlane.Model;
using Lakehold.ControlPlane.Security;
using Microsoft.EntityFrameworkCore;

namespace Lakehold.Api.PublicApi;

/// <summary>Marks a bounded JSON mutation as protected by the public idempotency contract.</summary>
public sealed class IdempotentMutationMetadata;

/// <summary>Marks an endpoint whose response contains a credential that must never be persisted.</summary>
public sealed class OneTimeSecretResponseMetadata;

public static class IdempotentMutationExtensions
{
    internal const int MaximumBodyBytes = 1024 * 1024;
    internal const int MaximumResponseBytes = 1024 * 1024;

    public static RouteHandlerBuilder WithIdempotency(this RouteHandlerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.WithMetadata(new IdempotentMutationMetadata());
        builder.Finally(RejectSecretResponseCaching);
        builder.AddEndpointFilter<PublicApiIdempotencyFilter>();
        return builder;
    }

    public static RouteHandlerBuilder WithOneTimeSecretResponse(this RouteHandlerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.WithMetadata(new OneTimeSecretResponseMetadata());
        builder.Finally(RejectSecretResponseCaching);
        return builder;
    }

    /// <summary>
    ///     Hashes only requests whose selected endpoint declares idempotency. It must run after
    ///     routing and before endpoint model binding.
    /// </summary>
    public static IApplicationBuilder UsePublicApiRequestHashing(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        return app.Use(async (context, next) =>
        {
            if (context.GetEndpoint()?.Metadata.GetMetadata<IdempotentMutationMetadata>() is null
                || !context.Request.Headers.TryGetValue("Idempotency-Key", out var key)
                || string.IsNullOrWhiteSpace(key))
            {
                await next(context).ConfigureAwait(false);
                return;
            }

            if (key.Count != 1 || key[0] is null || !IsValidKey(key[0]!))
            {
                await PublicApiProblems.Create(
                        context,
                        StatusCodes.Status400BadRequest,
                        "Idempotency-Key must contain one value of 16-128 visible ASCII characters.",
                        "invalid_idempotency_key")
                    .ExecuteAsync(context)
                    .ConfigureAwait(false);
                return;
            }

            context.Request.EnableBuffering(bufferThreshold: 64 * 1024, bufferLimit: MaximumBodyBytes);
            try
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                AppendHashSegment(hash, context.Request.ContentType ?? string.Empty);
                AppendHashSegment(hash, context.Request.QueryString.Value ?? string.Empty);
                var buffer = new byte[16 * 1024];
                int read;
                while ((read = await context.Request.Body.ReadAsync(buffer, context.RequestAborted)
                           .ConfigureAwait(false)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                }

                context.Items[PublicApiIdempotencyFilter.RequestHashItem] =
                    Convert.ToHexString(hash.GetHashAndReset());
                context.Request.Body.Position = 0;
            }
            catch (IOException)
            {
                await PublicApiProblems.Create(
                        context,
                        StatusCodes.Status413PayloadTooLarge,
                        "The idempotent request body exceeds the 1 MiB contract limit.",
                        "idempotency_body_too_large")
                    .ExecuteAsync(context)
                    .ConfigureAwait(false);
                return;
            }

            await next(context).ConfigureAwait(false);
        });
    }

    private static void AppendHashSegment(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }

    internal static bool IsValidKey(string value)
        => value.Length is >= 16 and <= 128
           && value.All(character => character is >= '!' and <= '~');

    private static void RejectSecretResponseCaching(EndpointBuilder endpoint)
    {
        if (endpoint.Metadata.OfType<IdempotentMutationMetadata>().Any()
            && endpoint.Metadata.OfType<OneTimeSecretResponseMetadata>().Any())
        {
            throw new InvalidOperationException(
                $"Endpoint '{endpoint.DisplayName}' cannot combine idempotency with a one-time secret response.");
        }
    }
}

public enum IdempotencyReservationOutcome
{
    Acquired,
    Replay,
    KeyConflict,
    InProgress,
}

public sealed record IdempotencyReservation(
    IdempotencyReservationOutcome Outcome,
    ApiIdempotencyRecord Record);

/// <summary>Persists and resolves idempotency keys in the shared control plane.</summary>
public sealed class PublicApiIdempotencyStore(ControlPlaneContext context, TimeProvider clock)
{
    public static readonly TimeSpan CompletedRecordRetention = TimeSpan.FromDays(7);

    public async Task<IdempotencyReservation> ReserveAsync(
        string scope,
        string key,
        string requestHash,
        CancellationToken cancellationToken)
    {
        var keyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        var existing = await context.ApiIdempotencyRecords
            .SingleOrDefaultAsync(
                record => record.Scope == scope && record.KeyHash == keyHash,
                cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            return Resolve(existing, requestHash);
        }

        var created = new ApiIdempotencyRecord
        {
            Scope = scope,
            KeyHash = keyHash,
            RequestHash = requestHash,
            Status = ApiIdempotencyStatus.InProgress,
            CreatedUtc = clock.GetUtcNow(),
        };
        context.ApiIdempotencyRecords.Add(created);
        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return new IdempotencyReservation(IdempotencyReservationOutcome.Acquired, created);
        }
        catch (DbUpdateException)
        {
            context.Entry(created).State = EntityState.Detached;
            existing = await context.ApiIdempotencyRecords
                .SingleAsync(
                    record => record.Scope == scope && record.KeyHash == keyHash,
                    cancellationToken)
                .ConfigureAwait(false);
            return Resolve(existing, requestHash);
        }
    }

    public async Task CompleteAsync(
        ApiIdempotencyRecord record,
        int statusCode,
        string? contentType,
        string? location,
        byte[] responseBody,
        CancellationToken cancellationToken)
    {
        record.Complete(statusCode, contentType, location, responseBody, clock.GetUtcNow());
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<int> DeleteExpiredCompletedAsync(CancellationToken cancellationToken)
    {
        var cutoff = clock.GetUtcNow().Subtract(CompletedRecordRetention);
        return context.ApiIdempotencyRecords
            .Where(record => record.Status == ApiIdempotencyStatus.Completed
                && record.CompletedUtc < cutoff)
            .ExecuteDeleteAsync(cancellationToken);
    }

    private static IdempotencyReservation Resolve(ApiIdempotencyRecord record, string requestHash)
    {
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(record.RequestHash),
                Convert.FromHexString(requestHash)))
        {
            return new IdempotencyReservation(IdempotencyReservationOutcome.KeyConflict, record);
        }

        return new IdempotencyReservation(
            record.Status == ApiIdempotencyStatus.Completed
                ? IdempotencyReservationOutcome.Replay
                : IdempotencyReservationOutcome.InProgress,
            record);
    }
}

/// <summary>Reserves a mutation key after authorization and replays a durable response when present.</summary>
public sealed class PublicApiIdempotencyFilter : IEndpointFilter
{
    public const string RequestHashItem = "lakehold.public-api.request-hash";

    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (!http.Request.Headers.TryGetValue("Idempotency-Key", out var keys)
            || keys.Count != 1
            || string.IsNullOrWhiteSpace(keys[0]))
        {
            return await next(context).ConfigureAwait(false);
        }

        if (http.Items[RequestHashItem] is not string requestHash)
        {
            return PublicApiProblems.Create(
                http,
                StatusCodes.Status400BadRequest,
                "The idempotent request could not be hashed.",
                "invalid_idempotency_key");
        }

        var principal = http.GetLakeholdPrincipal();
        var scope = CreateScope(http, principal);
        var store = http.RequestServices.GetRequiredService<PublicApiIdempotencyStore>();
        var reservation = await store.ReserveAsync(
                scope,
                keys[0]!,
                requestHash,
                http.RequestAborted)
            .ConfigureAwait(false);

        return reservation.Outcome switch
        {
            IdempotencyReservationOutcome.Replay => new CachedIdempotencyResult(reservation.Record),
            IdempotencyReservationOutcome.KeyConflict => PublicApiProblems.Create(
                http,
                StatusCodes.Status409Conflict,
                "The Idempotency-Key was already used with a different request.",
                "idempotency_key_reused"),
            IdempotencyReservationOutcome.InProgress => PublicApiProblems.Create(
                http,
                StatusCodes.Status409Conflict,
                "The Idempotency-Key belongs to an indeterminate or still-running request and will not be executed again.",
                "idempotency_in_progress"),
            _ => await WrapAsync(context, next, store, reservation.Record).ConfigureAwait(false),
        };
    }

    private static async Task<object?> WrapAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next,
        PublicApiIdempotencyStore store,
        ApiIdempotencyRecord record)
    {
        var result = PublicApiProblems.Normalize(
            await next(context).ConfigureAwait(false),
            context.HttpContext);
        return result is IResult httpResult
            ? new CapturingIdempotencyResult(httpResult, store, record)
            : throw new InvalidOperationException("An idempotent public API endpoint must return IResult.");
    }

    internal static string CreateScope(HttpContext http, ILakeholdPrincipal principal)
    {
        var caller = principal.TokenId is { } tokenId
            ? string.Concat(
                "token:",
                tokenId.ToString(System.Globalization.CultureInfo.InvariantCulture))
            : principal.IsDemo
                ? string.Concat("demo:", principal.TenantSlug, ":", principal.CatalogName)
                : string.Concat(
                    "oidc:",
                    http.User.FindFirst("sub")?.Value
                    ?? http.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                    ?? http.User.Identity?.Name
                    ?? throw new InvalidOperationException(
                        "An authenticated OIDC identity requires a stable subject for idempotency."));
        var callerHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(caller)));
        return string.Concat(callerHash, ":", http.Request.Method, ":", http.Request.Path);
    }
}

internal sealed class CapturingIdempotencyResult(
    IResult inner,
    PublicApiIdempotencyStore store,
    ApiIdempotencyRecord record) : IResult
{
    public async Task ExecuteAsync(HttpContext httpContext)
    {
        var originalBody = httpContext.Response.Body;
        await using var buffer = new BoundedMemoryStream(IdempotentMutationExtensions.MaximumResponseBytes);
        httpContext.Response.Body = buffer;
        try
        {
            await inner.ExecuteAsync(httpContext).ConfigureAwait(false);
            var body = buffer.ToArray();
            await store.CompleteAsync(
                    record,
                    httpContext.Response.StatusCode,
                    httpContext.Response.ContentType,
                    httpContext.Response.Headers.Location.ToString(),
                    body,
                    CancellationToken.None)
                .ConfigureAwait(false);
            httpContext.Response.Body = originalBody;
            httpContext.Response.ContentLength = body.Length;
            await originalBody.WriteAsync(body, httpContext.RequestAborted).ConfigureAwait(false);
        }
        finally
        {
            httpContext.Response.Body = originalBody;
        }
    }
}

internal sealed class BoundedMemoryStream(int maximumBytes) : MemoryStream
{
    public override void Write(byte[] buffer, int offset, int count)
    {
        EnsureWithinLimit(count);
        base.Write(buffer, offset, count);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        EnsureWithinLimit(buffer.Length);
        base.Write(buffer);
    }

    public override Task WriteAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        EnsureWithinLimit(count);
        return base.WriteAsync(buffer, offset, count, cancellationToken);
    }

    public override ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        EnsureWithinLimit(buffer.Length);
        return base.WriteAsync(buffer, cancellationToken);
    }

    public override void WriteByte(byte value)
    {
        EnsureWithinLimit(1);
        base.WriteByte(value);
    }

    public override void SetLength(long value)
    {
        if (value > maximumBytes)
        {
            throw ResponseTooLarge();
        }

        base.SetLength(value);
    }

    private void EnsureWithinLimit(int count)
    {
        if (Position > maximumBytes - count)
        {
            throw ResponseTooLarge();
        }
    }

    private static InvalidOperationException ResponseTooLarge()
        => new("An idempotent response exceeded the 1 MiB durable replay limit.");
}

internal sealed class CachedIdempotencyResult(ApiIdempotencyRecord record) : IResult
{
    public async Task ExecuteAsync(HttpContext httpContext)
    {
        if (record.Status != ApiIdempotencyStatus.Completed
            || record.ResponseStatusCode is null
            || record.ResponseBody is null)
        {
            throw new InvalidOperationException("The idempotency response is incomplete.");
        }

        httpContext.Response.StatusCode = record.ResponseStatusCode.Value;
        httpContext.Response.ContentType = record.ResponseContentType;
        httpContext.Response.ContentLength = record.ResponseBody.Length;
        if (!string.IsNullOrWhiteSpace(record.ResponseLocation))
        {
            httpContext.Response.Headers.Location = record.ResponseLocation;
        }
        httpContext.Response.Headers["Idempotency-Replayed"] = "true";
        await httpContext.Response.Body.WriteAsync(record.ResponseBody, httpContext.RequestAborted)
            .ConfigureAwait(false);
    }
}

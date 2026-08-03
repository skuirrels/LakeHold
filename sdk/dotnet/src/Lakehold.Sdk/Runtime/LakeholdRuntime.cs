using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lakehold.Sdk.Client;

namespace Lakehold.Sdk.Runtime;

/// <summary>Supported reliability behavior layered over the generated LakeHold v1 operations.</summary>
public static class LakeholdRuntime
{
    /// <summary>The source SDK version.</summary>
    public const string Version = "0.1.0";
    /// <summary>The product user-agent sent by the .NET runtime layer.</summary>
    public const string UserAgent = "lakehold-sdk/" + Version + " (.net)";
    /// <summary>The public response-correlation header.</summary>
    public const string RequestIdHeader = "X-Request-Id";

    /// <summary>Applies supported user-agent and whole-request timeout defaults.</summary>
    public static HttpClient Configure(HttpClient client, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        client.Timeout = timeout;
        client.DefaultRequestHeaders.UserAgent.Clear();
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return client;
    }

    /// <summary>Creates a cryptographically random caller idempotency key.</summary>
    public static string CreateIdempotencyKey() => Guid.NewGuid().ToString("N");

    /// <summary>Validates a caller-supplied idempotency key against the public contract.</summary>
    public static string ValidateIdempotencyKey(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length is < 16 or > 128 || value.Any(character => character is < '!' or > '~'))
        {
            throw new ArgumentException(
                "An idempotency key must contain 16-128 visible ASCII characters.",
                nameof(value));
        }
        return value;
    }

    /// <summary>Returns the server request identifier from response headers.</summary>
    public static string? RequestId(HttpResponseHeaders? headers)
        => headers is not null && headers.TryGetValues(RequestIdHeader, out var values)
            ? values.FirstOrDefault()
            : null;

    /// <summary>Converts any non-success generated response into a stable typed problem.</summary>
    public static LakeholdProblemException Problem(IApiResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        ProblemEnvelope? problem = null;
        try
        {
            problem = JsonSerializer.Deserialize<ProblemEnvelope>(response.RawContent, JsonOptions);
        }
        catch (JsonException)
        {
            // A proxy may return a non-problem body. Preserve the HTTP facts and use a stable fallback.
        }

        return new LakeholdProblemException(
            (int)response.StatusCode,
            problem?.Code ?? "request_failed",
            problem?.RequestId ?? RequestId(response.Headers),
            problem?.Detail,
            RetryAfter(response.Headers));
    }

    /// <summary>Executes a retry-safe generated call with bounded transient retries.</summary>
    public static async Task<TResponse> ExecuteWithRetryAsync<TResponse>(
        Func<CancellationToken, Task<TResponse>> call,
        RetryOptions options,
        bool retrySafe,
        CancellationToken cancellationToken = default)
        where TResponse : IApiResponse
    {
        ArgumentNullException.ThrowIfNull(call);
        options.Validate();
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = await call(cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return response;
            }

            if (!retrySafe || attempt >= options.MaximumRetries || !IsTransient(response.StatusCode))
            {
                throw Problem(response);
            }

            var delay = RetryAfter(response.Headers)
                ?? TimeSpan.FromMilliseconds(100 * (1 << Math.Min(attempt, 8)));
            delay = delay <= options.MaximumDelay ? delay : options.MaximumDelay;
            await options.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Lazily traverses opaque cursor pages without materialising the complete collection.</summary>
    public static async IAsyncEnumerable<T> PaginateAsync<T>(
        Func<string?, CancellationToken, Task<CursorPage<T>>> loader,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(loader);
        string? cursor = null;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await loader(cursor, cancellationToken).ConfigureAwait(false);
            if (page.Items.Count == 0 && page.NextCursor is not null)
            {
                throw new InvalidOperationException(
                    "A cursor page cannot be empty while advertising another page.");
            }
            foreach (var item in page.Items)
            {
                yield return item;
            }
            cursor = page.NextCursor;
        }
        while (cursor is not null);
    }

    /// <summary>Polls a durable operation until it reaches a terminal state.</summary>
    public static async Task<OperationSnapshot<T>> WaitForOperationAsync<T>(
        Func<CancellationToken, Task<OperationSnapshot<T>>> loader,
        TimeSpan timeout,
        TimeSpan pollInterval,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(loader);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(pollInterval, TimeSpan.Zero);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        while (true)
        {
            OperationSnapshot<T> operation;
            try
            {
                operation = await loader(deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("The operation did not complete before the timeout.");
            }

            switch (operation.Status.ToLowerInvariant())
            {
                case "succeeded":
                    return operation;
                case "failed":
                case "indeterminate":
                    throw new LakeholdOperationException(operation.Status, operation.Error);
                case "queued":
                case "running":
                    break;
                default:
                    throw new InvalidOperationException($"Unknown operation status '{operation.Status}'.");
            }

            try
            {
                await Task.Delay(pollInterval, deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("The operation did not complete before the timeout.");
            }
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static bool IsTransient(HttpStatusCode status)
        => status is HttpStatusCode.RequestTimeout
            or HttpStatusCode.TooManyRequests
            or HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout;

    private static TimeSpan? RetryAfter(HttpResponseHeaders headers)
    {
        var retry = headers.RetryAfter;
        if (retry?.Delta is { } delta)
        {
            return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        }
        if (retry?.Date is { } date)
        {
            var delay = date - DateTimeOffset.UtcNow;
            return delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
        }
        return null;
    }

    private sealed record ProblemEnvelope(
        [property: JsonPropertyName("code")] string Code,
        [property: JsonPropertyName("requestId")] string RequestId,
        [property: JsonPropertyName("detail")] string? Detail);
}

/// <summary>Bounded transient retry configuration.</summary>
public sealed record RetryOptions
{
    /// <summary>Creates retry configuration.</summary>
    public RetryOptions(
        int maximumRetries,
        TimeSpan maximumDelay,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        MaximumRetries = maximumRetries;
        MaximumDelay = maximumDelay;
        DelayAsync = delayAsync;
    }

    /// <summary>Maximum retries after the initial call.</summary>
    public int MaximumRetries { get; }

    /// <summary>Maximum accepted server or fallback delay.</summary>
    public TimeSpan MaximumDelay { get; }

    /// <summary>Optional delay implementation, primarily for deterministic tests.</summary>
    public Func<TimeSpan, CancellationToken, Task>? DelayAsync { get; }

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegative(MaximumRetries);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaximumRetries, 10);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(MaximumDelay, TimeSpan.Zero);
    }

    internal Task Delay(TimeSpan delay, CancellationToken cancellationToken)
        => DelayAsync?.Invoke(delay, cancellationToken) ?? Task.Delay(delay, cancellationToken);
}

/// <summary>One public API cursor page.</summary>
public sealed record CursorPage<T>
{
    /// <summary>Creates a cursor page.</summary>
    public CursorPage(IReadOnlyList<T> items, string? nextCursor)
    {
        Items = items ?? throw new ArgumentNullException(nameof(items));
        NextCursor = nextCursor;
    }

    /// <summary>Items in this page.</summary>
    public IReadOnlyList<T> Items { get; }
    /// <summary>Opaque cursor for the next page, or null at the end.</summary>
    public string? NextCursor { get; }
}

/// <summary>Language-neutral durable-operation state.</summary>
public sealed record OperationSnapshot<T>
{
    /// <summary>Creates an operation snapshot.</summary>
    public OperationSnapshot(string status, T? result, string? error)
    {
        Status = status ?? throw new ArgumentNullException(nameof(status));
        Result = result;
        Error = error;
    }

    /// <summary>Queued, running, succeeded, failed, or indeterminate.</summary>
    public string Status { get; }
    /// <summary>Terminal result when succeeded.</summary>
    public T? Result { get; }
    /// <summary>Safe terminal error when failed or indeterminate.</summary>
    public string? Error { get; }
}

/// <summary>Typed RFC 9457 failure returned by LakeHold.</summary>
public sealed class LakeholdProblemException : Exception
{
    /// <summary>Creates a typed public API problem.</summary>
    public LakeholdProblemException(
        int status,
        string code,
        string? requestId,
        string? detail,
        TimeSpan? retryAfter)
        : base(detail ?? code)
    {
        Status = status;
        Code = code;
        RequestId = requestId;
        Detail = detail;
        RetryAfter = retryAfter;
    }

    /// <summary>HTTP status.</summary>
    public int Status { get; }
    /// <summary>Stable LakeHold machine code.</summary>
    public string Code { get; }
    /// <summary>Server request identifier.</summary>
    public string? RequestId { get; }
    /// <summary>Bounded human-readable detail.</summary>
    public string? Detail { get; }
    /// <summary>Server-advertised retry delay.</summary>
    public TimeSpan? RetryAfter { get; }
}

/// <summary>A durable operation reached an unsuccessful terminal state.</summary>
public sealed class LakeholdOperationException : Exception
{
    /// <summary>Creates a terminal operation exception.</summary>
    public LakeholdOperationException(string status, string? error)
        : base(error ?? $"The operation ended with status '{status}'.")
    {
        Status = status;
    }

    /// <summary>Terminal operation status.</summary>
    public string Status { get; }
}

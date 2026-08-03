using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lakehold.Sdk.Client;

namespace Lakehold.Sdk.Runtime;

/// <summary>Supported reliability behavior layered over the generated LakeHold v1 operations.</summary>
public static class LakeholdRuntime
{
    private const int MaximumStreamRecordBytes = 64 * 1024 * 1024;
    private const int MaximumErrorBodyBytes = 1024 * 1024;
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
        var problem = DeserializeProblem(response.RawContent);

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

    /// <summary>Streams a read-only SQL result as schema, row, and completion events.</summary>
    public static IAsyncEnumerable<LakeholdStreamEvent> StreamQueryAsync(
        HttpClient client,
        Uri baseUri,
        string bearerToken,
        string tenant,
        string catalog,
        string sql,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        var body = JsonSerializer.Serialize(new { sql });
        return ReadNdjsonAsync(
            client,
            Request(
                HttpMethod.Post,
                baseUri,
                $"api/v1/tenants/{Segment(tenant)}/catalogs/{Segment(catalog)}/query:stream",
                bearerToken,
                new StringContent(body, Encoding.UTF8, "application/json")),
            "schema",
            cancellationToken);
    }

    /// <summary>Streams a finite table change range whose upper snapshot is frozen by the server.</summary>
    public static IAsyncEnumerable<LakeholdStreamEvent> StreamChangesAsync(
        HttpClient client,
        Uri baseUri,
        string bearerToken,
        string tenant,
        string catalog,
        string table,
        long fromSnapshot,
        string schema = "main",
        long? toSnapshot = null,
        int pageSize = 1000,
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(fromSnapshot);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pageSize, 10_000);
        var parameters = new List<string>
        {
            $"table={Uri.EscapeDataString(Required(table, nameof(table)))}",
            $"schema={Uri.EscapeDataString(Required(schema, nameof(schema)))}",
            $"fromSnapshot={fromSnapshot}",
            $"pageSize={pageSize}",
        };
        if (toSnapshot is { } upper)
        {
            parameters.Add($"toSnapshot={upper}");
        }
        if (!string.IsNullOrWhiteSpace(cursor))
        {
            parameters.Add($"cursor={Uri.EscapeDataString(cursor)}");
        }

        return ReadNdjsonAsync(
            client,
            Request(
                HttpMethod.Get,
                baseUri,
                $"api/v1/tenants/{Segment(tenant)}/catalogs/{Segment(catalog)}/changes:stream?{string.Join('&', parameters)}",
                bearerToken),
            "stream",
            cancellationToken);
    }

    private static async IAsyncEnumerable<LakeholdStreamEvent> ReadNdjsonAsync(
        HttpClient client,
        HttpRequestMessage request,
        string expectedFirstType,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        using (request)
        using (var response = await client
                   .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                   .ConfigureAwait(false))
        {
            if (!response.IsSuccessStatusCode)
            {
                var detail = await ReadBoundedBodyAsync(
                        response.Content,
                        MaximumErrorBodyBytes,
                        cancellationToken)
                    .ConfigureAwait(false);
                var problem = DeserializeProblem(detail);
                throw new LakeholdStreamException(
                    (int)response.StatusCode,
                    problem?.Code ?? "stream_request_failed",
                    problem?.RequestId ?? RequestId(response.Headers),
                    problem?.Detail ?? detail);
            }

            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var first = true;
            var completed = false;
            await foreach (var line in ReadNdjsonRecordsAsync(body, cancellationToken).ConfigureAwait(false))
            {
                using var document = JsonDocument.Parse(line);
                var payload = document.RootElement.Clone();
                var type = payload.TryGetProperty("type", out var kind) ? kind.GetString() : null;
                if (string.IsNullOrWhiteSpace(type))
                {
                    throw new InvalidDataException("A LakeHold stream record has no type discriminator.");
                }
                if (first && !string.Equals(type, expectedFirstType, StringComparison.Ordinal))
                {
                    throw new InvalidDataException($"Expected the stream to begin with '{expectedFirstType}', not '{type}'.");
                }
                first = false;
                if (string.Equals(type, "error", StringComparison.Ordinal))
                {
                    throw new LakeholdStreamException(
                        200,
                        payload.TryGetProperty("code", out var code) ? code.GetString() ?? "stream_failed" : "stream_failed",
                        payload.TryGetProperty("requestId", out var id) ? id.GetString() : null,
                        payload.TryGetProperty("detail", out var detail) ? detail.GetString() : null);
                }

                completed = string.Equals(type, "complete", StringComparison.Ordinal);
                yield return new LakeholdStreamEvent(type, payload);
            }

            if (!completed)
            {
                throw new EndOfStreamException("The LakeHold stream ended without a completion record.");
            }
        }
    }

    private static async IAsyncEnumerable<byte[]> ReadNdjsonRecordsAsync(
        Stream body,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var readBuffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        var record = new ArrayBufferWriter<byte>(64 * 1024);
        try
        {
            while (true)
            {
                var read = await body.ReadAsync(readBuffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }
                for (var index = 0; index < read; index++)
                {
                    var value = readBuffer[index];
                    if (value == (byte)'\n')
                    {
                        if (record.WrittenCount > 0)
                        {
                            yield return record.WrittenSpan.ToArray();
                            record.Clear();
                        }
                        continue;
                    }
                    if (record.WrittenCount >= MaximumStreamRecordBytes)
                    {
                        throw new InvalidDataException(
                            "A LakeHold stream record exceeded the 64 MiB client ceiling.");
                    }
                    var destination = record.GetSpan(1);
                    destination[0] = value;
                    record.Advance(1);
                }
            }
            if (record.WrittenCount > 0)
            {
                yield return record.WrittenSpan.ToArray();
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(readBuffer);
        }
    }

    private static async Task<string> ReadBoundedBodyAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[maximumBytes + 1];
        var count = 0;
        while (count < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(count), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            count += read;
        }
        var bounded = Math.Min(count, maximumBytes);
        return Encoding.UTF8.GetString(buffer, 0, bounded);
    }

    private static HttpRequestMessage Request(
        HttpMethod method,
        Uri baseUri,
        string relativePath,
        string bearerToken,
        HttpContent? content = null)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        var request = new HttpRequestMessage(method, new Uri(baseUri, relativePath)) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Required(bearerToken, nameof(bearerToken)));
        request.Headers.Accept.ParseAdd("application/x-ndjson");
        return request;
    }

    private static string Segment(string value) => Uri.EscapeDataString(Required(value, nameof(value)));

    private static string Required(string value, string parameter)
        => !string.IsNullOrWhiteSpace(value) ? value : throw new ArgumentException("A non-empty value is required.", parameter);

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

    private static ProblemEnvelope? DeserializeProblem(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ProblemEnvelope>(content, JsonOptions);
        }
        catch (JsonException)
        {
            // A proxy may return a non-problem body. Preserve the HTTP facts and use a stable fallback.
            return null;
        }
    }

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

/// <summary>One immutable NDJSON record from a LakeHold query or CDC stream.</summary>
public sealed record LakeholdStreamEvent(string Type, JsonElement Payload);

/// <summary>A stream request or terminal in-band record reported failure.</summary>
public sealed class LakeholdStreamException : Exception
{
    /// <summary>Creates a streaming transport or terminal-record failure.</summary>
    public LakeholdStreamException(int status, string code, string? requestId, string? detail)
        : base(detail ?? code)
    {
        Status = status;
        Code = code;
        RequestId = requestId;
        Detail = detail;
    }

    /// <summary>HTTP status, or 200 for an in-band terminal error.</summary>
    public int Status { get; }
    /// <summary>Stable machine-readable failure code.</summary>
    public string Code { get; }
    /// <summary>Server request identifier when supplied.</summary>
    public string? RequestId { get; }
    /// <summary>Bounded server or transport detail.</summary>
    public string? Detail { get; }
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

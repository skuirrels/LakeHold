using System.Net;
using System.Text;
using System.Text.Json;
using Lakehold.Sdk.Api;
using Lakehold.Sdk.Client;
using Lakehold.Sdk.Model;
using Lakehold.Sdk.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lakehold.Sdk.Tests;

public sealed class AuthenticationConformanceTests
{
    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web);

    private static readonly JsonDocument Fixture = JsonDocument.Parse(
        File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "conformance",
            "runtime-fixture.json")));

    [Fact]
    public void OneTimeTokenIsRedactedFromDiagnosticRendering()
    {
        var created = new CreatedTokenDto(7, "automation", "one-time-secret");

        Assert.DoesNotContain("one-time-secret", created.ToString(), StringComparison.Ordinal);
        Assert.Contains("<redacted>", created.ToString(), StringComparison.Ordinal);
        Assert.Equal("one-time-secret", created.Token);
    }

    [Fact]
    public async Task SendsBearerAuthenticationAndDeserializesTheAccessContract()
    {
        var handler = new RecordingHandler();
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://lakehold.test") };
        LakeholdRuntime.Configure(httpClient, TimeSpan.FromSeconds(5));
        var tokens = new TokenContainer<BearerToken>([new BearerToken("test-token")]);
        var api = new LakehouseApi(
            NullLogger<LakehouseApi>.Instance,
            NullLoggerFactory.Instance,
            httpClient,
            new JsonSerializerOptionsProvider(new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            new LakehouseApiEvents(),
            new RateLimitProvider<BearerToken>(tokens));

        var response = await api.GetApiV1AccessAsync();
        var access = response.Ok();

        Assert.Equal(HttpMethod.Get, handler.RequestMethod);
        Assert.Equal("https://lakehold.test/api/v1/access", handler.RequestUri?.ToString());
        Assert.Equal("Bearer", handler.AuthorizationScheme);
        Assert.Equal("test-token", handler.AuthorizationParameter);
        Assert.Equal(LakeholdRuntime.UserAgent, handler.UserAgent);
        Assert.NotNull(access);
        Assert.Equal("authenticated", access.Mode);
        Assert.Equal("reader", access.Role);
        Assert.True(access.ReadOnly);
        Assert.False(access.SystemAdmin);
    }

    [Fact]
    public async Task OrDefaultOperationsDoNotTurnCallerCancellationIntoNull()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var httpClient = new HttpClient(new CancellingHandler())
        {
            BaseAddress = new Uri("https://lakehold.test"),
        };
        var api = new LakehouseApi(
            NullLogger<LakehouseApi>.Instance,
            NullLoggerFactory.Instance,
            httpClient,
            new JsonSerializerOptionsProvider(new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            new LakehouseApiEvents(),
            new RateLimitProvider<BearerToken>(
                new TokenContainer<BearerToken>([new BearerToken("test-token")])));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => api.GetApiV1AccessOrDefaultAsync(cancellation.Token));
    }

    [Fact]
    public async Task SharedReliabilityFixtureIsImplemented()
    {
        var requestId = Fixture.RootElement.GetProperty("requestId").GetString()!;
        var problemBody = Fixture.RootElement.GetProperty("problem").GetRawText();
        var rateLimited = Response(HttpStatusCode.TooManyRequests, problemBody, requestId, 2);
        var problem = LakeholdRuntime.Problem(rateLimited);
        Assert.Equal("rate_limited", problem.Code);
        Assert.Equal(requestId, problem.RequestId);
        Assert.Equal(TimeSpan.FromSeconds(2), problem.RetryAfter);

        var attempts = 0;
        var delays = new List<TimeSpan>();
        var response = await LakeholdRuntime.ExecuteWithRetryAsync(
            _ => Task.FromResult(++attempts < 3 ? rateLimited : Response(HttpStatusCode.OK, "{}")),
            new RetryOptions(2, TimeSpan.FromSeconds(30), (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            }),
            retrySafe: true);
        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal(3, attempts);
        Assert.Equal([TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2)], delays);

        var unsafeAttempts = 0;
        await Assert.ThrowsAsync<LakeholdProblemException>(() => LakeholdRuntime.ExecuteWithRetryAsync(
            _ => Task.FromResult(++unsafeAttempts == 1 ? rateLimited : Response(HttpStatusCode.OK, "{}")),
            new RetryOptions(2, TimeSpan.FromSeconds(30)),
            retrySafe: false));
        Assert.Equal(1, unsafeAttempts);

        var items = new List<int>();
        await foreach (var item in LakeholdRuntime.PaginateAsync<int>((cursor, _) => Task.FromResult(
            cursor is null
                ? new CursorPage<int>([1, 2], "cursor-2")
                : new CursorPage<int>([3], null))))
        {
            items.Add(item);
        }
        Assert.Equal([1, 2, 3], items);

        var states = new Queue<string>(["queued", "running", "succeeded"]);
        var operation = await LakeholdRuntime.WaitForOperationAsync(
            _ => Task.FromResult(new OperationSnapshot<string>(states.Dequeue(), "result", null)),
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMilliseconds(1));
        Assert.Equal("result", operation.Result);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LakeholdRuntime.WaitForOperationAsync<string>(
            _ => Task.FromResult(new OperationSnapshot<string>("running", null, null)),
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMilliseconds(1),
            cancelled.Token));

        Assert.Equal(
            Fixture.RootElement.GetProperty("idempotencyKey").GetString(),
            LakeholdRuntime.ValidateIdempotencyKey(
                Fixture.RootElement.GetProperty("idempotencyKey").GetString()!));
        foreach (var invalid in new[] { "too-short", "0123456789abcde ", "0123456789abcde\t", "0123456789abcdeé" })
        {
            Assert.Throws<ArgumentException>(() => LakeholdRuntime.ValidateIdempotencyKey(invalid));
        }

        var additive = JsonSerializer.Deserialize<AccessDto>(
            Fixture.RootElement.GetProperty("additiveAccess").GetRawText(),
            SerializerOptions);
        Assert.Equal("authenticated", additive?.Mode);
    }

    [Fact]
    public async Task StreamingFixtureIsConsumedIncrementally()
    {
        var handler = new StreamingHandler("query-stream.ndjson");
        using var client = new HttpClient(handler);
        var events = new List<LakeholdStreamEvent>();

        await foreach (var item in LakeholdRuntime.StreamQueryAsync(
                           client,
                           new Uri("https://lakehold.test/"),
                           "test-token",
                           "tenant one",
                           "catalog/one",
                           "select 1"))
        {
            events.Add(item);
        }

        Assert.Equal(["schema", "row", "row", "complete"], events.Select(item => item.Type));
        Assert.Equal(HttpMethod.Post, handler.RequestMethod);
        Assert.Equal(
            "https://lakehold.test/api/v1/tenants/tenant%20one/catalogs/catalog%2Fone/query:stream",
            handler.RequestUri?.AbsoluteUri);
        Assert.Equal("Bearer test-token", handler.Authorization);
        Assert.Equal("application/x-ndjson", handler.Accept);
    }

    [Fact]
    public async Task ChangeStreamingFixtureIsConsumedIncrementally()
    {
        var handler = new StreamingHandler("change-stream.ndjson");
        using var client = new HttpClient(handler);
        var events = new List<LakeholdStreamEvent>();

        await foreach (var item in LakeholdRuntime.StreamChangesAsync(
                           client,
                           new Uri("https://lakehold.test/"),
                           "test-token",
                           "tenant one",
                           "catalog/one",
                           "orders current",
                           10,
                           toSnapshot: 12,
                           pageSize: 1))
        {
            events.Add(item);
        }

        Assert.Equal(["stream", "change", "change", "complete"], events.Select(item => item.Type));
        Assert.Equal(HttpMethod.Get, handler.RequestMethod);
        Assert.Contains("table=orders%20current", handler.RequestUri?.Query, StringComparison.Ordinal);
        Assert.Contains("fromSnapshot=10", handler.RequestUri?.Query, StringComparison.Ordinal);
        Assert.Contains("toSnapshot=12", handler.RequestUri?.Query, StringComparison.Ordinal);
        Assert.Contains("pageSize=1", handler.RequestUri?.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StreamingHandshakePreservesPublicProblem()
    {
        var requestId = Fixture.RootElement.GetProperty("requestId").GetString()!;
        var problemBody = Fixture.RootElement.GetProperty("problem").GetRawText();
        using var client = new HttpClient(new ProblemStreamingHandler(problemBody, requestId));

        var exception = await Assert.ThrowsAsync<LakeholdStreamException>(async () =>
        {
            await foreach (var _ in LakeholdRuntime.StreamQueryAsync(
                               client,
                               new Uri("https://lakehold.test/"),
                               "test-token",
                               "tenant",
                               "catalog",
                               "SELECT 1"))
            {
            }
        });

        Assert.Equal((int)HttpStatusCode.TooManyRequests, exception.Status);
        Assert.Equal("rate_limited", exception.Code);
        Assert.Equal(requestId, exception.RequestId);
        Assert.Equal("Retry after the advertised delay.", exception.Detail);
    }

    [Fact]
    public async Task ReleasedServerStreamingConformance()
    {
        var endpoint = Environment.GetEnvironmentVariable("LAKEHOLD_CONFORMANCE_URL");
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return;
        }

        var token = RequiredEnvironment("LAKEHOLD_CONFORMANCE_TOKEN");
        var tenant = RequiredEnvironment("LAKEHOLD_CONFORMANCE_TENANT");
        var catalog = RequiredEnvironment("LAKEHOLD_CONFORMANCE_CATALOG");
        using var client = LakeholdRuntime.Configure(new HttpClient(), TimeSpan.FromSeconds(30));
        var types = new List<string>();
        await foreach (var item in LakeholdRuntime.StreamQueryAsync(
                           client,
                           new Uri(endpoint.EndsWith('/') ? endpoint : endpoint + "/"),
                           token,
                           tenant,
                           catalog,
                           "SELECT 1 AS conformance"))
        {
            types.Add(item.Type);
        }

        Assert.Equal(["schema", "row", "complete"], types);
    }

    private static string RequiredEnvironment(string name)
        => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"{name} is required when released-server conformance is enabled.");

    private static Lakehold.Sdk.Client.ApiResponse Response(
        HttpStatusCode status,
        string content,
        string? requestId = null,
        int? retryAfterSeconds = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "https://lakehold.test/api/v1/conformance");
        var response = new HttpResponseMessage(status) { RequestMessage = request };
        if (requestId is not null)
        {
            response.Headers.TryAddWithoutValidation(LakeholdRuntime.RequestIdHeader, requestId);
        }
        if (retryAfterSeconds is not null)
        {
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(
                TimeSpan.FromSeconds(retryAfterSeconds.Value));
        }
        return new Lakehold.Sdk.Client.ApiResponse(
            request,
            response,
            content,
            "/api/v1/conformance",
            DateTime.UtcNow,
            SerializerOptions);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public HttpMethod? RequestMethod { get; private set; }

        public Uri? RequestUri { get; private set; }

        public string? AuthorizationScheme { get; private set; }

        public string? AuthorizationParameter { get; private set; }

        public string? UserAgent { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestMethod = request.Method;
            RequestUri = request.RequestUri;
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            AuthorizationParameter = request.Headers.Authorization?.Parameter;
            UserAgent = request.Headers.UserAgent.ToString();
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"mode\":\"authenticated\",\"role\":\"reader\",\"readOnly\":true,\"systemAdmin\":false}",
                    Encoding.UTF8,
                    "application/json"),
                RequestMessage = request,
            };
            return Task.FromResult(response);
        }
    }

    private sealed class CancellingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromCanceled<HttpResponseMessage>(cancellationToken);
    }

    private sealed class StreamingHandler(string fixtureName) : HttpMessageHandler
    {
        public HttpMethod? RequestMethod { get; private set; }
        public Uri? RequestUri { get; private set; }
        public string? Authorization { get; private set; }
        public string? Accept { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestMethod = request.Method;
            RequestUri = request.RequestUri;
            Authorization = request.Headers.Authorization?.ToString();
            Accept = request.Headers.Accept.Single().MediaType;
            var content = File.ReadAllText(Path.Combine(
                AppContext.BaseDirectory,
                "conformance",
                fixtureName));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/x-ndjson"),
                RequestMessage = request,
            });
        }
    }

    private sealed class ProblemStreamingHandler(string body, string requestId) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/problem+json"),
                RequestMessage = request,
            };
            response.Headers.TryAddWithoutValidation(LakeholdRuntime.RequestIdHeader, requestId);
            return Task.FromResult(response);
        }
    }
}

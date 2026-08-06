using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Lakehold.Querying;

namespace Lakehold.Api.Querying;

/// <summary>Discovers optional planners and dispatches source without exposing LakeHold credentials.</summary>
public sealed class QueryPlannerRegistry(
    IHttpClientFactory clients,
    IOptions<QueryPlannerOptions> options,
    QueryPlannerDescriptorCache descriptors,
    ILogger<QueryPlannerRegistry> logger) : IQuerySourcePlanner
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan DiscoveryTimeout = TimeSpan.FromSeconds(1);
    private const string SqlStarter = """
        SELECT *
        FROM main.events
        LIMIT 100;
        """;
    private readonly QueryPlannerOptions _options = options.Value;

    public async Task<IReadOnlyList<QueryLanguageDescriptor>> GetLanguagesAsync(CancellationToken cancellationToken)
    {
        // SQL is executed by this process, so it has no health to report and can never be absent.
        var languages = new List<QueryLanguageDescriptor>
        {
            new(
                QueryPlannerOptions.BuiltInLanguageId,
                "SQL",
                "sql",
                SqlStarter,
                ReadOnly: false,
                SupportsSavedQueries: true),
        };

        foreach (var planner in _options.Planners)
        {
            languages.Add(await DescribeAsync(planner, cancellationToken).ConfigureAwait(false));
        }

        return languages;
    }

    /// <summary>
    ///     Health-checks one configured planner and describes it either way. An unhealthy planner is
    ///     reported unavailable with an operator-actionable reason rather than dropped: dropping it
    ///     is what made a compiler that missed a deadline look like a product that was never built.
    /// </summary>
    private async Task<QueryLanguageDescriptor> DescribeAsync(
        ExternalQueryPlannerOptions planner,
        CancellationToken cancellationToken)
    {
        try
        {
            using var healthTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            healthTimeout.CancelAfter(DiscoveryTimeout);
            using var request = CreateRequest(planner, HttpMethod.Get, "descriptor");
            using var response = await clients.CreateClient(nameof(QueryPlannerRegistry))
                .SendAsync(request, healthTimeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return Unavailable(planner, StatusReason(response.StatusCode));
            }

            var descriptor = await ReadBoundedAsync<QueryLanguageDescriptor>(response, healthTimeout.Token)
                .ConfigureAwait(false);
            if (descriptor is null)
            {
                return Unavailable(planner, "The planner returned an empty descriptor.");
            }

            if (!string.Equals(descriptor.Id, planner.Id, StringComparison.Ordinal))
            {
                return Unavailable(
                    planner,
                    "The planner reports a different language id than the one it is configured under.");
            }

            // Availability is the host's judgement, never the plugin's claim about itself.
            var healthy = descriptor with { Available = true, UnavailableReason = null };
            descriptors.Remember(healthy);
            return healthy;
        }
        catch (HttpRequestException exception)
        {
            // ReadBoundedAsync reports an unreadable or oversized body as a transport failure, so the
            // inner exception is the only thing separating "nothing answered" from "something did".
            return Unavailable(
                planner,
                exception.InnerException is InvalidDataException or JsonException
                    ? "The planner returned a descriptor this version of LakeHold cannot read."
                    : "The planner is not reachable.",
                exception);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            return Unavailable(
                planner,
                $"The planner did not answer within the {DiscoveryTimeout.TotalSeconds:0.#}s discovery deadline.",
                exception);
        }
    }

    /// <summary>
    ///     Describes a planner that failed discovery, reusing its last known descriptor so the
    ///     language keeps its name and editor mode while it is down.
    /// </summary>
    private QueryLanguageDescriptor Unavailable(
        ExternalQueryPlannerOptions planner,
        string reason,
        Exception? exception = null)
    {
        // The reason reaches a browser, so it is curated text about the deployment, never a planner's
        // own bytes or a URL: an endpoint is internal topology and a descriptor is untrusted input.
        QueryPlannerLog.PlannerUnavailable(logger, planner.Id, reason, exception);
        var known = descriptors.Get(planner.Id)
            ?? new QueryLanguageDescriptor(
                planner.Id,
                planner.Id,
                "text",
                string.Empty,
                ReadOnly: true,
                SupportsSavedQueries: false);

        return known with
        {
            // Read-only and unsaveable regardless of what the planner advertised when it was up:
            // nothing can be planned, so nothing can be run or published either.
            ReadOnly = true,
            SupportsSavedQueries = false,
            Available = false,
            UnavailableReason = reason,
        };
    }

    private static string StatusReason(System.Net.HttpStatusCode status) => status switch
    {
        System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden =>
            "The planner rejected this deployment's planner key. Check that the API and the planner "
            + "were given the same shared secret.",
        System.Net.HttpStatusCode.NotFound =>
            "The configured endpoint serves no planner descriptor. Check that it is the planner's "
            + "base address and ends in '/'.",
        System.Net.HttpStatusCode.TooManyRequests or System.Net.HttpStatusCode.ServiceUnavailable =>
            "The planner is starting up or is saturated with work.",
        _ => $"The planner answered HTTP {(int)status} to discovery.",
    };

    public async Task<QueryPlan> PlanAsync(
        string language,
        QueryPlanningRequest planningRequest,
        CancellationToken cancellationToken)
    {
        if (string.Equals(language, QueryPlannerOptions.BuiltInLanguageId, StringComparison.Ordinal))
        {
            return new QueryPlan(planningRequest.Source, [], [], planningRequest.SchemaFingerprint);
        }

        var planner = _options.Planners.SingleOrDefault(candidate =>
            string.Equals(candidate.Id, language, StringComparison.Ordinal));
        if (planner is null)
        {
            throw new QueryLanguageUnavailableException(language);
        }

        try
        {
            using var request = CreateRequest(planner, HttpMethod.Post, "plan");
            request.Content = JsonContent.Create(planningRequest);
            using var response = await clients.CreateClient(nameof(QueryPlannerRegistry))
                .SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.BadRequest)
            {
                var failure = await ReadBoundedAsync<QueryPlanningFailure>(response, cancellationToken)
                    .ConfigureAwait(false);
                throw new QuerySourceInvalidException(failure?.Diagnostics ?? []);
            }

            response.EnsureSuccessStatusCode();
            return await ReadBoundedAsync<QueryPlan>(response, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Planner '{language}' returned an empty response.");
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new HttpRequestException($"Planner '{language}' timed out.", exception);
        }
    }

    public async Task<QueryLanguageStarter> CreateStarterAsync(
        string language,
        QueryCatalogSchema catalogSchema,
        CancellationToken cancellationToken)
    {
        if (string.Equals(language, QueryPlannerOptions.BuiltInLanguageId, StringComparison.Ordinal))
        {
            return new QueryLanguageStarter(SqlStarter, catalogSchema.SchemaFingerprint);
        }

        var planner = _options.Planners.SingleOrDefault(candidate =>
            string.Equals(candidate.Id, language, StringComparison.Ordinal));
        if (planner is null)
        {
            throw new QueryLanguageUnavailableException(language);
        }

        try
        {
            using var request = CreateRequest(planner, HttpMethod.Post, "starter");
            request.Content = JsonContent.Create(catalogSchema);
            using var response = await clients.CreateClient(nameof(QueryPlannerRegistry))
                .SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.BadRequest)
            {
                var failure = await ReadBoundedAsync<QueryPlanningFailure>(response, cancellationToken)
                    .ConfigureAwait(false);
                throw new QuerySourceInvalidException(failure?.Diagnostics ?? []);
            }

            response.EnsureSuccessStatusCode();
            return await ReadBoundedAsync<QueryLanguageStarter>(response, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Planner '{language}' returned an empty starter response.");
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new HttpRequestException($"Planner '{language}' timed out.", exception);
        }
    }

    private static HttpRequestMessage CreateRequest(
        ExternalQueryPlannerOptions planner,
        HttpMethod method,
        string relativePath)
    {
        var request = new HttpRequestMessage(method, new Uri(planner.Endpoint, relativePath));
        if (!string.IsNullOrEmpty(planner.SharedSecret))
        {
            request.Headers.Add("X-Lakehold-Planner-Key", planner.SharedSecret);
        }

        return request;
    }

    private async Task<T?> ReadBoundedAsync<T>(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var contentLength = response.Content.Headers.ContentLength;
        if (contentLength.HasValue && contentLength.Value > _options.MaxResponseBytes)
        {
            // Carries the same inner exception as the mid-read refusal below, because that is what
            // DescribeAsync classifies on: without it, a planner answering with an oversized body it
            // declared up front is reported unreachable, and the operator goes looking at a network
            // that is working.
            throw Oversized();
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var bounded = new BoundedReadStream(source, _options.MaxResponseBytes);
        try
        {
            return await JsonSerializer.DeserializeAsync<T>(
                bounded,
                Json,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException)
        {
            throw new HttpRequestException(exception.Message, exception);
        }

        HttpRequestException Oversized()
        {
            var limit = new InvalidDataException(
                $"Planner response exceeded the {_options.MaxResponseBytes:N0}-byte limit.");
            return new HttpRequestException(limit.Message, limit);
        }
    }

    private sealed class BoundedReadStream(Stream inner, long limit) : Stream
    {
        private long _read;

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
            => Track(inner.Read(buffer, offset, count));

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
            => Track(await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));

        private int Track(int count)
        {
            _read += count;
            if (_read > limit)
            {
                throw new InvalidDataException($"Planner response exceeded the {limit:N0}-byte limit.");
            }
            return count;
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

internal static partial class QueryPlannerLog
{
    [LoggerMessage(
        EventId = 4400,
        Level = LogLevel.Warning,
        Message = "Query planner {PlannerId} failed discovery and is offered as unavailable: {Reason}")]
    public static partial void PlannerUnavailable(
        ILogger logger,
        string plannerId,
        string reason,
        Exception? exception);
}

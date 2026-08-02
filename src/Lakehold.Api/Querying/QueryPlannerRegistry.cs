using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Lakehold.Querying;

namespace Lakehold.Api.Querying;

/// <summary>Discovers optional planners and dispatches source without exposing LakeHold credentials.</summary>
public sealed class QueryPlannerRegistry(
    IHttpClientFactory clients,
    IOptions<QueryPlannerOptions> options) : IQuerySourcePlanner
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string SqlStarter = """
        SELECT *
        FROM main.events
        LIMIT 100;
        """;
    private readonly QueryPlannerOptions _options = options.Value;

    public async Task<IReadOnlyList<QueryLanguageDescriptor>> GetLanguagesAsync(CancellationToken cancellationToken)
    {
        var languages = new List<QueryLanguageDescriptor>
        {
            new("sql", "SQL", "sql", SqlStarter, ReadOnly: false, SupportsSavedQueries: true),
        };

        foreach (var planner in _options.Planners)
        {
            try
            {
                using var healthTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                healthTimeout.CancelAfter(TimeSpan.FromSeconds(1));
                using var request = CreateRequest(planner, HttpMethod.Get, "descriptor");
                using var response = await clients.CreateClient(nameof(QueryPlannerRegistry))
                    .SendAsync(request, healthTimeout.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }

                var descriptor = await ReadBoundedAsync<QueryLanguageDescriptor>(response, cancellationToken)
                    .ConfigureAwait(false);
                if (descriptor is not null && string.Equals(descriptor.Id, planner.Id, StringComparison.Ordinal))
                {
                    languages.Add(descriptor);
                }
            }
            catch (HttpRequestException)
            {
                // Optional planner health must never degrade SQL.
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // An optional planner that misses the discovery deadline is treated as unhealthy.
            }
            catch (System.Text.Json.JsonException)
            {
                // A planner with an incompatible descriptor is omitted, like any unhealthy plugin.
            }
        }

        return languages;
    }

    public async Task<QueryPlan> PlanAsync(
        string language,
        QueryPlanningRequest planningRequest,
        CancellationToken cancellationToken)
    {
        if (string.Equals(language, "sql", StringComparison.Ordinal))
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
        if (string.Equals(language, "sql", StringComparison.Ordinal))
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
            throw new HttpRequestException(
                $"Planner response exceeded the {_options.MaxResponseBytes:N0}-byte limit.");
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

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Lakehold.Api.Security;
using Lakehold.ControlPlane.Model;
using Microsoft.Extensions.Options;

namespace Lakehold.Api.Connectors;

/// <summary>
///     HubSpot Contacts reader with renewable OAuth credentials, rate-limited requests, and adaptive
///     time windows that remain below HubSpot's 10,000-result search ceiling.
/// </summary>
internal sealed class HubSpotContactsDataConnectorSource(
    IHttpClientFactory clients,
    IOptions<ConnectorOptions> options,
    ConnectorSecretResolver secrets,
    TimeProvider clock,
    HubSpotRequestLimiter requestLimiter) : IDataConnectorSource
{
    public const string HttpClientName = "lakehold-hubspot-connectors";
    private const int MaxRateLimitRetries = 3;
    private static readonly Uri TokenEndpoint = new("https://api.hubapi.com/oauth/v1/token");

    public ConnectorAdapterManifest Manifest { get; } = new(
        "lakehold.hubspot-contacts",
        1,
        DataConnectorKind.HubSpot,
        new HashSet<DataConnectorReadMode> { DataConnectorReadMode.Incremental },
        new HashSet<DataConnectorAuthenticationKind> { DataConnectorAuthenticationKind.OAuthRefreshToken },
        SupportsSourceVersion: true);

    public async Task<ConnectorSourceResult> ReadAsync(
        ConnectorReadContext context,
        IDataConnectorRecordWriter destination,
        CancellationToken cancellationToken)
    {
        var endpoint = new Uri(context.Connector.EndpointUrl, UriKind.Absolute);
        if (!string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(endpoint.DnsSafeHost, "api.hubapi.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The HubSpot adapter endpoint must use https://api.hubapi.com.");
        }

        var searchEndpoint = new Uri(endpoint, "/crm/v3/objects/contacts/search");
        var authentication = context.Connector.Authentication();
        if (authentication.Kind != DataConnectorAuthenticationKind.OAuthRefreshToken)
        {
            throw new InvalidOperationException("The HubSpot adapter requires OAuth refresh-token authentication.");
        }

        var accessToken = await RenewAccessTokenAsync(context, authentication, cancellationToken)
            .ConfigureAwait(false);
        var settings = context.Connector.SourceSettings();
        var properties = (settings.Properties ?? ["email", "firstname", "lastname", "lastmodifieddate"])
            .Select(value => value.Trim())
            .Where(value => value.Length > 0)
            .Append("lastmodifieddate")
            .Append("hs_object_id")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(100)
            .ToArray();
        var pageSize = Math.Clamp(settings.PageSize, 1, 200);
        var safeUpper = ToHubSpotPrecision(clock.GetUtcNow() - options.Value.HubSpotIndexingDelay);
        var checkpoint = ParseCheckpoint(context.Checkpoint);
        var lowerExclusive = checkpoint is { } current
            ? ToHubSpotPrecision(current - options.Value.HubSpotCheckpointOverlap)
            : await DiscoverInitialLowerBoundAsync(
                    context,
                    accessToken,
                    searchEndpoint,
                    properties,
                    cancellationToken)
                .ConfigureAwait(false);
        if (lowerExclusive is null)
        {
            return new ConnectorSourceResult(context.Checkpoint, context.Checkpoint);
        }

        if (safeUpper <= lowerExclusive.Value)
        {
            return new ConnectorSourceResult(context.Checkpoint, context.Checkpoint);
        }

        var windowUpper = await SelectWindowUpperAsync(
                context,
                accessToken,
                searchEndpoint,
                properties,
                lowerExclusive.Value,
                safeUpper,
                cancellationToken)
            .ConfigureAwait(false);

        string? after = null;
        var pageCount = 0;
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);
        var seenContactIds = new HashSet<string>(StringComparer.Ordinal);
        int? expectedTotal = null;
        var received = 0;
        do
        {
            pageCount++;
            if (pageCount > options.Value.MaxPaginationPages)
            {
                throw new InvalidDataException(
                    $"HubSpot pagination exceeded the {options.Value.MaxPaginationPages}-page limit.");
            }

            var page = await SearchAsync(
                    context,
                    accessToken,
                    searchEndpoint,
                    properties,
                    pageSize,
                    lowerExclusive.Value,
                    windowUpper,
                    after,
                    cancellationToken)
                .ConfigureAwait(false);
            if (page.Total > options.Value.MaxHubSpotResultsPerWindow)
            {
                throw new InvalidDataException(
                    "HubSpot changed while the bounded search window was being read; retrying will select a smaller window.");
            }
            if (expectedTotal is { } establishedTotal && page.Total != establishedTotal)
            {
                throw new InvalidDataException(
                    "HubSpot changed the result count while the bounded window was being read; the window was not checkpointed.");
            }

            expectedTotal ??= page.Total;

            foreach (var contact in page.Results)
            {
                _ = ContactUpdatedAt(contact);
                if (!contact.TryGetProperty("id", out var id)
                    || id.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(id.GetString()))
                {
                    throw new InvalidDataException("HubSpot returned a contact without a stable record id.");
                }
                if (!seenContactIds.Add(id.GetString()!))
                {
                    throw new InvalidDataException(
                        "HubSpot returned a duplicate contact while the bounded window was being read; the window was not checkpointed.");
                }

                await destination.WriteAsync(contact.GetRawText(), cancellationToken).ConfigureAwait(false);
                received++;
            }

            after = page.After;
            if (after is not null && !seenCursors.Add(after))
            {
                throw new InvalidDataException("HubSpot returned a repeated pagination cursor.");
            }
        }
        while (after is not null);

        if (received != expectedTotal)
        {
            throw new InvalidDataException(
                $"HubSpot returned {received} of {expectedTotal ?? 0} contacts in the bounded window; the window was not checkpointed.");
        }

        var proposedCheckpoint = windowUpper.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        return new ConnectorSourceResult(
            proposedCheckpoint,
            proposedCheckpoint,
            $"{context.Checkpoint ?? "<initial>"}->{proposedCheckpoint}");
    }

    private async Task<DateTimeOffset?> DiscoverInitialLowerBoundAsync(
        ConnectorReadContext context,
        string accessToken,
        Uri endpoint,
        string[] properties,
        CancellationToken cancellationToken)
    {
        var first = await SearchAsync(
                context,
                accessToken,
                endpoint,
                properties,
                limit: 1,
                lowerExclusive: null,
                upperInclusive: null,
                after: null,
                cancellationToken)
            .ConfigureAwait(false);
        return first.Results.Length == 0
            ? null
            : ToHubSpotPrecision(ContactUpdatedAt(first.Results[0]).AddMilliseconds(-1));
    }

    private async Task<DateTimeOffset> SelectWindowUpperAsync(
        ConnectorReadContext context,
        string accessToken,
        Uri endpoint,
        string[] properties,
        DateTimeOffset lowerExclusive,
        DateTimeOffset maximumUpper,
        CancellationToken cancellationToken)
    {
        var total = await CountWindowAsync(
                context,
                accessToken,
                endpoint,
                properties,
                lowerExclusive,
                maximumUpper,
                cancellationToken)
            .ConfigureAwait(false);
        if (total <= options.Value.MaxHubSpotResultsPerWindow)
        {
            return maximumUpper;
        }

        var lowMilliseconds = lowerExclusive.ToUnixTimeMilliseconds();
        var highMilliseconds = maximumUpper.ToUnixTimeMilliseconds();
        DateTimeOffset? best = null;
        for (var iteration = 0; iteration < 48 && highMilliseconds - lowMilliseconds > 1; iteration++)
        {
            var midpointMilliseconds = lowMilliseconds + ((highMilliseconds - lowMilliseconds) / 2);
            var midpoint = DateTimeOffset.FromUnixTimeMilliseconds(midpointMilliseconds);
            total = await CountWindowAsync(
                    context,
                    accessToken,
                    endpoint,
                    properties,
                    lowerExclusive,
                    midpoint,
                    cancellationToken)
                .ConfigureAwait(false);
            if (total <= options.Value.MaxHubSpotResultsPerWindow)
            {
                best = midpoint;
                lowMilliseconds = midpointMilliseconds;
            }
            else
            {
                highMilliseconds = midpointMilliseconds;
            }
        }

        if (best is null || best <= lowerExclusive)
        {
            throw new InvalidDataException(
                $"More than {options.Value.MaxHubSpotResultsPerWindow} HubSpot contacts share the smallest safe checkpoint window; use the HubSpot export API for this backlog.");
        }

        return best.Value;
    }

    private async Task<int> CountWindowAsync(
        ConnectorReadContext context,
        string accessToken,
        Uri endpoint,
        string[] properties,
        DateTimeOffset lowerExclusive,
        DateTimeOffset upperInclusive,
        CancellationToken cancellationToken) => (await SearchAsync(
            context,
            accessToken,
            endpoint,
            properties,
            limit: 1,
            lowerExclusive,
            upperInclusive,
            after: null,
            cancellationToken).ConfigureAwait(false)).Total;

    private async Task<HubSpotSearchPage> SearchAsync(
        ConnectorReadContext context,
        string accessToken,
        Uri endpoint,
        string[] properties,
        int limit,
        DateTimeOffset? lowerExclusive,
        DateTimeOffset? upperInclusive,
        string? after,
        CancellationToken cancellationToken)
    {
        var body = BuildSearchRequest(lowerExclusive, upperInclusive, properties, limit, after);
        using var document = await SendJsonAsync(
                () =>
                {
                    var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
                    {
                        Content = new StringContent(body, Encoding.UTF8, "application/json"),
                    };
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                    return request;
                },
                endpoint,
                options.Value.MaxSnapshotBytes,
                cancellationToken)
            .ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("results", out var results)
            || results.ValueKind != JsonValueKind.Array
            || !document.RootElement.TryGetProperty("total", out var totalElement)
            || !totalElement.TryGetInt32(out var total)
            || total < 0)
        {
            throw new InvalidDataException("HubSpot returned an invalid contacts response.");
        }

        var next = document.RootElement.TryGetProperty("paging", out var paging)
                   && paging.TryGetProperty("next", out var nextElement)
                   && nextElement.TryGetProperty("after", out var afterElement)
            ? afterElement.GetString()
            : null;
        return new HubSpotSearchPage(results.EnumerateArray().Select(item => item.Clone()).ToArray(), total, next);
    }

    private async Task<string> RenewAccessTokenAsync(
        ConnectorReadContext context,
        DataConnectorAuthentication authentication,
        CancellationToken cancellationToken)
    {
        var clientId = await secrets.ResolveAsync(
                authentication.ClientIdSecretReference
                ?? throw new InvalidOperationException("HubSpot OAuth requires a client-id secret reference."),
                context.TenantSlug,
                context.CatalogName,
                TokenEndpoint.DnsSafeHost,
                cancellationToken)
            .ConfigureAwait(false);
        var clientSecret = await secrets.ResolveAsync(
                authentication.ClientSecretReference
                ?? throw new InvalidOperationException("HubSpot OAuth requires a client-secret reference."),
                context.TenantSlug,
                context.CatalogName,
                TokenEndpoint.DnsSafeHost,
                cancellationToken)
            .ConfigureAwait(false);
        var refreshToken = await secrets.ResolveAsync(
                authentication.RefreshTokenSecretReference
                ?? throw new InvalidOperationException("HubSpot OAuth requires a refresh-token secret reference."),
                context.TenantSlug,
                context.CatalogName,
                TokenEndpoint.DnsSafeHost,
                cancellationToken)
            .ConfigureAwait(false);
        using var document = await SendJsonAsync(
                () => new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
                {
                    Content = new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        ["grant_type"] = "refresh_token",
                        ["client_id"] = clientId,
                        ["client_secret"] = clientSecret,
                        ["refresh_token"] = refreshToken,
                    }),
                },
                TokenEndpoint,
                1024 * 1024,
                cancellationToken)
            .ConfigureAwait(false);
        var token = document.RootElement.Deserialize<OAuthTokenResponse>();
        return token?.AccessToken
            ?? throw new InvalidDataException("HubSpot returned an invalid OAuth token response.");
    }

    private async Task<JsonDocument> SendJsonAsync(
        Func<HttpRequestMessage> requestFactory,
        Uri endpoint,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        var resolution = await OutboundDestinationPolicy.ResolveAsync(
                endpoint,
                options.Value,
                "HubSpot connector",
                cancellationToken)
            .ConfigureAwait(false);
        if (resolution.Error is not null)
        {
            throw new InvalidOperationException(resolution.Error);
        }

        for (var attempt = 0; ; attempt++)
        {
            await requestLimiter.WaitAsync(cancellationToken).ConfigureAwait(false);
            using var request = requestFactory();
            if (resolution.Address is not null)
            {
                request.Options.Set(OutboundConnection.ApprovedAddress, resolution.Address);
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.Value.RequestTimeout);
            using var response = await clients.CreateClient(HttpClientName)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < MaxRateLimitRetries)
            {
                var delay = response.Headers.RetryAfter?.Delta
                            ?? (response.Headers.RetryAfter?.Date - clock.GetUtcNow())
                            ?? TimeSpan.FromSeconds(1);
                await Task.Delay(delay > TimeSpan.Zero ? delay : TimeSpan.FromSeconds(1), clock, cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > maxBytes)
            {
                throw new InvalidDataException("The HubSpot response exceeds the connector response limit.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            await using var limited = new LimitedReadStream(stream, maxBytes);
            return await JsonDocument.ParseAsync(limited, cancellationToken: timeout.Token).ConfigureAwait(false);
        }
    }

    private static string BuildSearchRequest(
        DateTimeOffset? lowerExclusive,
        DateTimeOffset? upperInclusive,
        string[] properties,
        int limit,
        string? after)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("limit", limit);
            if (after is not null)
            {
                writer.WriteString("after", after);
            }

            writer.WritePropertyName("properties");
            writer.WriteStartArray();
            foreach (var property in properties)
            {
                writer.WriteStringValue(property);
            }

            writer.WriteEndArray();
            writer.WritePropertyName("sorts");
            writer.WriteStartArray();
            writer.WriteStringValue("lastmodifieddate");
            writer.WriteEndArray();
            if (lowerExclusive is not null || upperInclusive is not null)
            {
                writer.WritePropertyName("filterGroups");
                writer.WriteStartArray();
                writer.WriteStartObject();
                writer.WritePropertyName("filters");
                writer.WriteStartArray();
                WriteDateFilter(writer, "GT", lowerExclusive);
                WriteDateFilter(writer, "LTE", upperInclusive);
                writer.WriteEndArray();
                writer.WriteEndObject();
                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteDateFilter(Utf8JsonWriter writer, string operation, DateTimeOffset? value)
    {
        if (value is null)
        {
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("propertyName", "lastmodifieddate");
        writer.WriteString("operator", operation);
        writer.WriteString("value", value.Value.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
        writer.WriteEndObject();
    }

    private static DateTimeOffset? ParseCheckpoint(string? checkpoint) => string.IsNullOrWhiteSpace(checkpoint)
        ? null
        : ToHubSpotPrecision(
            DateTimeOffset.Parse(checkpoint, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));

    private static DateTimeOffset ToHubSpotPrecision(DateTimeOffset value) =>
        DateTimeOffset.FromUnixTimeMilliseconds(value.ToUnixTimeMilliseconds());

    private static DateTimeOffset ContactUpdatedAt(JsonElement contact)
    {
        if (!contact.TryGetProperty("updatedAt", out var updatedAt)
            || updatedAt.ValueKind != JsonValueKind.String
            || !DateTimeOffset.TryParse(
                updatedAt.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var value))
        {
            throw new InvalidDataException("HubSpot returned a contact without a valid updatedAt value.");
        }

        return value;
    }

    private sealed record HubSpotSearchPage(JsonElement[] Results, int Total, string? After);

    private sealed record OAuthTokenResponse(
        [property: System.Text.Json.Serialization.JsonPropertyName("access_token")] string AccessToken);
}

/// <summary>
///     Shares HubSpot request pacing across every connector execution on this node. HubSpot also
///     returns Retry-After for cross-node/account contention, which the source honours separately.
/// </summary>
internal sealed class HubSpotRequestLimiter(
    IOptions<ConnectorOptions> options,
    TimeProvider clock) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long? _lastRequestTimestamp;

    public async Task WaitAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_lastRequestTimestamp is { } previous)
            {
                var remaining = options.Value.HubSpotMinimumRequestInterval - clock.GetElapsedTime(previous);
                if (remaining > TimeSpan.Zero)
                {
                    await Task.Delay(remaining, clock, cancellationToken).ConfigureAwait(false);
                }
            }

            _lastRequestTimestamp = clock.GetTimestamp();
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}

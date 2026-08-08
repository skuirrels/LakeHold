using Lakehold.Api.Security;
using System.Net;

namespace Lakehold.Api.Connectors;

/// <summary>Limits and egress policy for managed connector refreshes.</summary>
public sealed class ConnectorOptions : IOutboundDestinationOptions
{
    public const string SectionName = "Lakehold:Connectors";

    public bool Enabled { get; set; } = true;

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(15);

    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(5);

    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromMinutes(2);

    public int MaxConcurrentRuns { get; set; } = 2;

    public long MaxSnapshotBytes { get; set; } = 512L * 1024 * 1024;

    public long MaxRows { get; set; } = 1_000_000;

    public int MaxPaginationPages { get; set; } = 10_000;

    /// <summary>Maximum HubSpot search results committed by one bounded time window.</summary>
    public int MaxHubSpotResultsPerWindow { get; set; } = 9_000;

    /// <summary>Delay behind wall-clock time to allow HubSpot's search index to become visible.</summary>
    public TimeSpan HubSpotIndexingDelay { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Replay overlap applied to every HubSpot checkpoint; keyed upsert removes duplicates.</summary>
    public TimeSpan HubSpotCheckpointOverlap { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Minimum spacing between HubSpot requests; the documented account limit is five/second.</summary>
    public TimeSpan HubSpotMinimumRequestInterval { get; set; } = TimeSpan.FromMilliseconds(250);

    public int MaxRecordBytes { get; set; } = 16 * 1024 * 1024;

    public long MaxAggregateScratchBytes { get; set; } = 1024L * 1024 * 1024;

    public long MinimumFreeBytes { get; set; } = 1024L * 1024 * 1024;

    public TimeSpan StaleFileAge { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Disposable node-local staging only; durable data is committed before deletion.</summary>
    public string ScratchRoot { get; set; } = string.Empty;

    public bool AllowHttp { get; set; }

    public bool AllowUnsafeDestinations { get; set; }

    public string[] AllowedHosts { get; set; } = [];

    public string? SecretProviderEndpoint { get; set; }

    public string? SecretProviderTokenEnvironmentVariable { get; set; }

    /// <summary>
    ///     Operator-approved credential bindings. A tenant-authored connector may resolve a secret
    ///     only when its tenant, catalog, reference, and destination host match one of these rows.
    /// </summary>
    public ConnectorSecretBindingOptions[] SecretBindings { get; set; } = [];

    /// <summary>
    /// Mandatory deployment-owned egress path for Kafka Avro. Kafka clients follow advertised
    /// listeners, so the worker connects only to a literal-IP TCP gateway that owns the permitted
    /// broker routes. The gateway may tunnel through SOCKS, but that transport is deployment
    /// infrastructure rather than an unsupported librdkafka client setting.
    /// </summary>
    public KafkaEgressGatewayOptions? KafkaEgressGateway { get; set; }

    public bool TryGetKafkaEgressGateway(out KafkaEgressGatewayOptions gateway, out string? error)
    {
        gateway = KafkaEgressGateway ?? new KafkaEgressGatewayOptions();
        if (string.IsNullOrWhiteSpace(gateway.PolicyName)
            || !TryParseHostPort(gateway.KafkaBootstrapGateway, out _)
            || !Uri.TryCreate(gateway.SchemaRegistryHttpProxy, UriKind.Absolute, out var registryProxy)
            || (registryProxy.Scheme != Uri.UriSchemeHttp && registryProxy.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(registryProxy.UserInfo)
            || !IPAddress.TryParse(registryProxy.DnsSafeHost, out _))
        {
            error = "Kafka Avro is disabled until the deployment configures a named egress policy, a literal-IP Kafka TCP gateway, and a literal-IP HTTP(S) proxy for Schema Registry.";
            return false;
        }

        error = null;
        return true;
    }

    private static bool TryParseHostPort(string? value, out IPEndPoint? endpoint)
    {
        endpoint = null;
        if (!Uri.TryCreate($"socks5://{value}", UriKind.Absolute, out var uri)
            || uri.Port is < 1 or > 65535
            || !string.IsNullOrEmpty(uri.UserInfo)
            || uri.AbsolutePath != "/"
            || !IPAddress.TryParse(uri.DnsSafeHost, out var address))
        {
            return false;
        }

        endpoint = new IPEndPoint(address, uri.Port);
        return true;
    }
}

public sealed class KafkaEgressGatewayOptions
{
    /// <summary>Operator name for the gateway policy that permits the connector's Kafka destinations.</summary>
    public string PolicyName { get; set; } = string.Empty;

    /// <summary>
    /// Literal IPv4/IPv6 address and port of a deployment-owned Kafka TCP gateway. It must route
    /// every advertised listener under this policy and may use SOCKS internally.
    /// </summary>
    public string KafkaBootstrapGateway { get; set; } = string.Empty;

    /// <summary>HTTP(S) proxy URI with a literal IP host for Schema Registry HTTPS traffic.</summary>
    public string SchemaRegistryHttpProxy { get; set; } = string.Empty;

    /// <summary>
    /// Deployment-managed PEM bundle used to verify a private Schema Registry certificate.
    /// This is a path inside the running service, never tenant-supplied connector state.
    /// </summary>
    public string? SchemaRegistryCaCertificatePath { get; set; }
}

/// <summary>A fail-closed grant to use one external secret at one connector destination.</summary>
public sealed class ConnectorSecretBindingOptions
{
    public string TenantSlug { get; set; } = string.Empty;

    public string CatalogName { get; set; } = string.Empty;

    public string Reference { get; set; } = string.Empty;

    public string DestinationHost { get; set; } = string.Empty;
}

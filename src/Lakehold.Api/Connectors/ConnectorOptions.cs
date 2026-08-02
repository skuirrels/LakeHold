using Lakehold.Api.Security;

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
}

/// <summary>A fail-closed grant to use one external secret at one connector destination.</summary>
public sealed class ConnectorSecretBindingOptions
{
    public string TenantSlug { get; set; } = string.Empty;

    public string CatalogName { get; set; } = string.Empty;

    public string Reference { get; set; } = string.Empty;

    public string DestinationHost { get; set; } = string.Empty;
}

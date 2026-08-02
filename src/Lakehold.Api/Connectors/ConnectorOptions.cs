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

    public int MaxRecordBytes { get; set; } = 16 * 1024 * 1024;

    public long MaxAggregateScratchBytes { get; set; } = 1024L * 1024 * 1024;

    public long MinimumFreeBytes { get; set; } = 1024L * 1024 * 1024;

    public TimeSpan StaleFileAge { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Disposable node-local staging only; durable data is committed before deletion.</summary>
    public string ScratchRoot { get; set; } = string.Empty;

    public bool AllowHttp { get; set; }

    public bool AllowUnsafeDestinations { get; set; }

    public string[] AllowedHosts { get; set; } = [];
}

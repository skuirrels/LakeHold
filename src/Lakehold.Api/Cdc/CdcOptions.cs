namespace Lakehold.Api.Cdc;

/// <summary>Configuration for outbound change-data-capture deliveries.</summary>
public sealed class CdcOptions
{
    public const string SectionName = "Lakehold:Cdc";

    /// <summary>Whether the dispatcher runs at all. Off leaves the pull API as the only CDC surface.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    ///     How often subscriptions are polled for new snapshots.
    /// </summary>
    /// <remarks>
    ///     This is the upper bound on delivery latency, not a delivery schedule: a poll that finds
    ///     no new snapshot does nothing. Polling the metadata catalog is one <c>max(snapshot_id)</c>
    ///     query per subscribed catalog, so a short interval is cheap.
    /// </remarks>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    ///     Ceiling on changes carried inline per table per delivery. A window with more sets the
    ///     payload's <c>truncated</c> flag, and the consumer pulls the remainder from the changes API.
    /// </summary>
    /// <remarks>
    ///     A webhook is a notification with the common case inlined, not a bulk-transfer channel. An
    ///     unbounded payload would make one large backfill able to wedge every consumer behind it.
    /// </remarks>
    public int MaxChangesPerTable { get; set; } = 1_000;

    /// <summary>Wall-clock ceiling for one webhook post.</summary>
    public TimeSpan DeliveryTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    ///     Ceiling for the exponential backoff applied to a failing subscription. Backoff doubles per
    ///     consecutive failure from <see cref="PollInterval"/> up to this cap, so a dead endpoint
    ///     costs one request per cap interval rather than one per poll.
    /// </summary>
    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromMinutes(30);
}

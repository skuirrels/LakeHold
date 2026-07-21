using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Lakehold.Engine.Telemetry;

/// <summary>
///     The single <see cref="ActivitySource"/> and <see cref="Meter"/> for Lakehold's own spans and
///     instruments, plus the tag keys they use.
/// </summary>
/// <remarks>
///     <para>
///         HTTP and runtime instrumentation shows that a request took 400 ms; it cannot say whether
///         that was DuckDB executing, a cold session attaching, or the request queued behind another
///         statement on the same tenant's gate. Those three have completely different remedies, so
///         they are measured separately here.
///     </para>
///     <para>
///         <b>Submitted SQL is never recorded.</b> A statement is tenant data and may carry literals
///         from their tables, so no span attribute, metric tag, or log message carries it — the
///         catalog, row count, and outcome are enough to find a slow query without exporting its
///         text. For the same reason nothing here records a metadata source, data path, or secret
///         name: those are credential-bearing or credential-adjacent.
///     </para>
///     <para>
///         <b>Metric tags stay low-cardinality.</b> Tenant and catalog identify a time series per
///         tenant, which is exactly how a metrics backend's cardinality budget is exhausted on a
///         multi-tenant node. They are recorded as span attributes, where per-request cardinality is
///         normal and a slow tenant is still findable by trace. Metrics carry only bounded tags —
///         operation, outcome, result, reason.
///     </para>
/// </remarks>
public static class LakeholdTelemetry
{
    /// <summary>Name of the activity source. Register with <c>tracing.AddSource(...)</c>.</summary>
    public const string ActivitySourceName = "Lakehold";

    /// <summary>Name of the meter. Register with <c>metrics.AddMeter(...)</c>.</summary>
    public const string MeterName = "Lakehold";

    /// <summary>Spans for query execution, session start-up, and maintenance.</summary>
    public static readonly ActivitySource Source = new(ActivitySourceName);

    private static readonly Meter Meter = new(MeterName);

    // ---- queries ----

    /// <summary>End-to-end duration of a tenant statement, including queue time and streaming.</summary>
    public static readonly Histogram<double> QueryDuration = Meter.CreateHistogram<double>(
        "lakehold.query.duration", "s", "Duration of a tenant statement.");

    /// <summary>Rows returned to the caller, after the result ceiling is applied.</summary>
    public static readonly Histogram<long> QueryRows = Meter.CreateHistogram<long>(
        "lakehold.query.rows", "{row}", "Rows returned by a tenant statement, after truncation.");

    /// <summary>Statements that hit <c>MaxRowsPerResult</c> and were truncated.</summary>
    public static readonly Counter<long> QueriesTruncated = Meter.CreateCounter<long>(
        "lakehold.query.truncated", "{query}", "Statements truncated at the result-row ceiling.");

    // ---- sessions ----

    /// <summary>
    ///     Time spent waiting for a session's gate before a statement could run.
    /// </summary>
    /// <remarks>
    ///     The signal that distinguishes "this tenant's queries are slow" from "this tenant's queries
    ///     are queued behind each other". A Duckling is single-writer by design, so sustained queue
    ///     time is the load signal that says a tenant needs its own node or a read replica — and it
    ///     is invisible in request duration alone.
    /// </remarks>
    public static readonly Histogram<double> SessionQueueDuration = Meter.CreateHistogram<double>(
        "lakehold.session.queue.duration", "s", "Time a statement waited for its session's gate.");

    /// <summary>Cost of a cold session: extensions, attach, and select.</summary>
    public static readonly Histogram<double> SessionStartDuration = Meter.CreateHistogram<double>(
        "lakehold.session.start.duration", "s", "Time to start a compute session for a catalog.");

    /// <summary>Warm sessions currently held by the node.</summary>
    public static readonly UpDownCounter<long> WarmSessions = Meter.CreateUpDownCounter<long>(
        "lakehold.sessions.warm", "{session}", "Compute sessions currently warm on this node.");

    /// <summary>Sessions evicted, tagged by why. See <see cref="ReasonKey"/>.</summary>
    public static readonly Counter<long> SessionEvictions = Meter.CreateCounter<long>(
        "lakehold.session.evictions", "{session}", "Compute sessions evicted from the pool.");

    /// <summary>
    ///     Pool lookups, tagged hit or miss. The miss rate is the warm-session ceiling's report card:
    ///     a rising miss rate against a steady tenant count means sessions are being evicted and
    ///     restarted rather than reused.
    /// </summary>
    public static readonly Counter<long> PoolRequests = Meter.CreateCounter<long>(
        "lakehold.pool.requests", "{request}", "Session pool lookups, by hit or miss.");

    // ---- control plane ----

    /// <summary>
    ///     Catalog resolutions, tagged hit or miss. A miss is a control-plane round trip; a sustained
    ///     miss rate means something is invalidating far more than it should.
    /// </summary>
    public static readonly Counter<long> CatalogCacheRequests = Meter.CreateCounter<long>(
        "lakehold.catalog.cache.requests", "{request}", "Catalog resolutions, by cache hit or miss.");

    // ---- maintenance ----

    /// <summary>Duration of a maintenance operation, tagged by operation and outcome.</summary>
    public static readonly Histogram<double> MaintenanceDuration = Meter.CreateHistogram<double>(
        "lakehold.maintenance.duration", "s", "Duration of a catalog maintenance operation.");

    /// <summary>
    ///     Scheduled maintenance lease attempts, tagged <c>acquired</c> or <c>held_elsewhere</c>.
    /// </summary>
    /// <remarks>
    ///     On a multi-node deployment every node fires the same schedule and all but one stand down.
    ///     Without this, "the backup ran somewhere" and "the backup ran nowhere" produce the same
    ///     silence on any individual node — and a schedule that has quietly stopped running is the
    ///     failure an operator finds out about during a restore.
    /// </remarks>
    public static readonly Counter<long> MaintenanceLeaseAttempts = Meter.CreateCounter<long>(
        "lakehold.maintenance.lease.attempts", "{attempt}", "Scheduled maintenance lease attempts.");

    // ---- tag keys ----

    /// <summary>Tenant slug. Spans only — see the cardinality note on this class.</summary>
    public const string TenantKey = "lakehold.tenant";

    /// <summary>Catalog name. Spans only — see the cardinality note on this class.</summary>
    public const string CatalogKey = "lakehold.catalog";

    /// <summary>Rows returned, after truncation.</summary>
    public const string RowsKey = "lakehold.rows";

    /// <summary>Whether the result hit the row ceiling.</summary>
    public const string TruncatedKey = "lakehold.truncated";

    /// <summary>Where the catalog's metadata lives.</summary>
    public const string MetadataKindKey = "lakehold.metadata_kind";

    /// <summary>Maintenance operation name.</summary>
    public const string OperationKey = "lakehold.operation";

    /// <summary>Whether a destructive operation only reported what it would do.</summary>
    public const string DryRunKey = "lakehold.dry_run";

    /// <summary><c>success</c> or <c>error</c>.</summary>
    public const string OutcomeKey = "lakehold.outcome";

    /// <summary><c>hit</c> or <c>miss</c>.</summary>
    public const string ResultKey = "lakehold.result";

    /// <summary>Why a session was evicted: <c>idle</c>, <c>overflow</c>, or <c>explicit</c>.</summary>
    public const string ReasonKey = "lakehold.reason";

    /// <summary>Tag value for a successful operation.</summary>
    public const string OutcomeSuccess = "success";

    /// <summary>Tag value for a failed operation.</summary>
    public const string OutcomeError = "error";

    /// <summary>Tag value for a cache or pool hit.</summary>
    public const string ResultHit = "hit";

    /// <summary>Tag value for a cache or pool miss.</summary>
    public const string ResultMiss = "miss";
}

using System.Text;
using System.Text.Json;
using Lakehold.ControlPlane.Data;
using Lakehold.ControlPlane.Model;
using Lakehold.Engine.Catalog;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Lakehold.Api.Cdc;

/// <summary>One row-level change in a webhook delivery.</summary>
/// <param name="SnapshotId">The snapshot that committed the change.</param>
/// <param name="RowId">DuckLake's stable row identity, pairing an update's pre- and post-image.</param>
/// <param name="ChangeType">
///     <c>insert</c>, <c>delete</c>, <c>update_preimage</c>, or <c>update_postimage</c> — the feed's
///     own vocabulary, passed through so receivers are not coupled to this assembly's enum.
/// </param>
/// <param name="Row">The table's columns for this change, as JSON-safe wire values.</param>
public sealed record ChangeDeliveryRow(
    long SnapshotId,
    long RowId,
    string ChangeType,
    IReadOnlyDictionary<string, object?> Row);

/// <summary>One table's changes in a webhook delivery.</summary>
/// <param name="Truncated">
///     True when the window held more changes than the per-table ceiling. The payload carries a
///     prefix; the consumer pulls the full window from the changes API using the payload's snapshot
///     range.
/// </param>
public sealed record ChangeDeliveryTable(
    string Schema,
    string Table,
    bool Truncated,
    IReadOnlyList<ChangeDeliveryRow> Changes);

/// <summary>The body of one webhook delivery: a catalog's changes over one snapshot window.</summary>
public sealed record ChangeDeliveryPayload(
    long SubscriptionId,
    string Catalog,
    long FromSnapshot,
    long ToSnapshot,
    DateTimeOffset DeliveredUtc,
    IReadOnlyList<ChangeDeliveryTable> Tables);

/// <summary>
///     Polls subscribed catalogs for new snapshots and posts their changes to each subscription's
///     endpoint — change data capture with no Debezium, no Kafka, and no separate pipeline.
/// </summary>
/// <remarks>
///     <para>
///         DuckLake already records what every snapshot changed, so CDC here is reading bookkeeping
///         that exists anyway, not tailing a WAL. The dispatcher polls the newest snapshot id per
///         subscribed catalog (one scalar query), and only reads actual changes when the cursor is
///         behind.
///     </para>
///     <para>
///         Delivery is <em>at-least-once</em>: the cursor advances only after a 2xx response, so a
///         crash between post and cursor write re-sends the window. The payload's snapshot range
///         makes receiver-side dedup cheap. Ordering is per subscription, oldest window first; a
///         failing subscription backs off exponentially without holding back others.
///     </para>
///     <para>
///         A window can exceed the per-table payload ceiling — a backfill, a bulk delete. The
///         delivery then carries a truncated prefix and the flag, and the consumer pulls the rest
///         from the changes API. The alternative — unbounded payloads — lets one bulk operation
///         wedge every consumer; silently dropping the excess would be worse. The cursor still
///         advances on 2xx because the receiver has been told, verifiably, what range it is
///         responsible for.
///     </para>
///     <para>
///         Webhook payloads carry table data to a tenant-configured endpoint; that is the feature,
///         not a leak. What must never appear in a payload, a log, or an error is the subscription's
///         signing secret or any storage credential — the payload is built solely from the change
///         feed, and errors record status codes and exception messages only.
///     </para>
///     <para>
///         In a multi-node deployment every node runs this dispatcher, so a window can be delivered
///         once per node. That is deliberate: the cursor write races are harmless (last write is the
///         same value), the contract is at-least-once either way, and a per-subscription lease would
///         put a coordination round-trip in front of every delivery to remove duplicates the
///         receiver must already tolerate. Run with <c>Lakehold:Cdc:Enabled=false</c> on all but one
///         node where duplicate notifications are unacceptable.
///     </para>
/// </remarks>
public sealed class ChangeFeedDispatcher(
    IServiceScopeFactory scopeFactory,
    IHttpClientFactory httpClientFactory,
    IOptions<CdcOptions> options,
    ILogger<ChangeFeedDispatcher> logger) : BackgroundService
{
    /// <summary>Named HTTP client, configured with the delivery timeout at registration.</summary>
    public const string HttpClientName = "lakehold-cdc";

    // Web defaults camel-case property names, matching the API's own JSON so a consumer sees one
    // convention everywhere. The serialised bytes are what gets signed, so sign-then-send uses this
    // exact buffer rather than re-serialising.
    private static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        using var timer = new PeriodicTimer(settings.PollInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The sweep must survive anything a single tick throws — a transient control-plane
                // error killing the hosted service would silently end CDC for every tenant.
                CdcLog.SweepFailed(logger, ex);
            }

            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                {
                    break;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Runs one delivery pass over every active subscription.</summary>
    /// <remarks>Internal so tests can drive a pass directly instead of waiting on the timer.</remarks>
    internal async Task SweepAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneContext>();
        var lakehouse = scope.ServiceProvider.GetRequiredService<LakehouseService>();
        var settings = options.Value;
        var now = DateTimeOffset.UtcNow;

        // Tracked, because delivery outcomes are written back to each row. Tenant comes along so the
        // catalog can be resolved through the same isolation seam queries use.
        var subscriptions = await db.ChangeSubscriptions
            .Include(s => s.Tenant)
            .Where(s => s.Active)
            .OrderBy(s => s.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var subscription in subscriptions)
        {
            if (InBackoff(subscription, settings, now))
            {
                continue;
            }

            try
            {
                await DeliverPendingAsync(lakehouse, subscription, settings, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One subscription's failure must not starve the rest of the sweep.
                subscription.ConsecutiveFailures++;
                subscription.LastAttemptUtc = DateTimeOffset.UtcNow;
                subscription.LastError = Truncate(ex.Message);
                CdcLog.DeliveryFailed(logger, ex, subscription.Id, subscription.CatalogName);
            }

            // Saved per subscription rather than per sweep, so a crash mid-sweep loses at most one
            // subscription's cursor progress — and at-least-once delivery absorbs exactly that.
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Delivers the window between a subscription's cursor and the catalog's newest snapshot.</summary>
    private async Task DeliverPendingAsync(
        LakehouseService lakehouse,
        ChangeSubscription subscription,
        CdcOptions settings,
        CancellationToken cancellationToken)
    {
        var tenant = subscription.Tenant.Slug;
        var catalog = subscription.CatalogName;

        var latest = await lakehouse.GetLatestSnapshotAsync(tenant, catalog, cancellationToken).ConfigureAwait(false);
        if (latest is null || latest.Value <= subscription.LastDeliveredSnapshot)
        {
            return;
        }

        var from = subscription.LastDeliveredSnapshot + 1;
        var to = latest.Value;

        var watched = subscription.TableName is { Length: > 0 }
            ? [(subscription.SchemaName, subscription.TableName)]
            : await lakehouse.ListChangeTablesAsync(tenant, catalog, cancellationToken).ConfigureAwait(false);

        var tables = new List<ChangeDeliveryTable>();
        foreach (var (schema, table) in watched)
        {
            var page = await lakehouse
                .GetChangesAsync(tenant, catalog, schema, table, from, to, settings.MaxChangesPerTable, cancellationToken)
                .ConfigureAwait(false);

            if (page.Changes.Count == 0 && !page.Truncated)
            {
                continue;
            }

            tables.Add(new ChangeDeliveryTable(
                schema,
                table,
                page.Truncated,
                [.. page.Changes.Select(c => new ChangeDeliveryRow(c.SnapshotId, c.RowId, ToWireName(c.Change), c.Row))]));
        }

        if (tables.Count == 0)
        {
            // The new snapshots touched nothing this subscription watches — a maintenance commit, or
            // writes to other tables. Advance the cursor without posting, or every later sweep would
            // rescan the same empty window forever.
            subscription.LastDeliveredSnapshot = to;
            return;
        }

        var payload = new ChangeDeliveryPayload(
            subscription.Id, catalog, from, to, DateTimeOffset.UtcNow, tables);

        await PostAsync(subscription, payload, settings, cancellationToken).ConfigureAwait(false);

        subscription.LastDeliveredSnapshot = to;
        subscription.ConsecutiveFailures = 0;
        subscription.LastError = null;
        subscription.LastAttemptUtc = DateTimeOffset.UtcNow;

        var deliveredChanges = tables.Sum(t => t.Changes.Count);
        var anyTruncated = tables.Any(t => t.Truncated);
        CdcLog.Delivered(logger, subscription.Id, catalog, from, to, deliveredChanges, anyTruncated);
    }

    private async Task PostAsync(
        ChangeSubscription subscription,
        ChangeDeliveryPayload payload,
        CdcOptions settings,
        CancellationToken cancellationToken)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(payload, PayloadJson);

        using var request = new HttpRequestMessage(HttpMethod.Post, subscription.EndpointUrl);
        request.Content = new ByteArrayContent(body);
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json")
        {
            CharSet = Encoding.UTF8.WebName,
        };

        // The signature covers the exact bytes on the wire, so the receiver verifies before parsing.
        request.Headers.TryAddWithoutValidation(WebhookSigner.SignatureHeader, WebhookSigner.Compute(body, subscription.Secret));
        request.Headers.TryAddWithoutValidation(WebhookSigner.DeliveryHeader, Guid.NewGuid().ToString("N"));

        var client = httpClientFactory.CreateClient(HttpClientName);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(settings.DeliveryTimeout);

        using var response = await client.SendAsync(request, timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            // The status line is the receiver's answer; the body is not read because an arbitrary
            // endpoint's response has no business flowing into logs or the subscription row.
            throw new HttpRequestException(
                $"Endpoint returned {(int)response.StatusCode} {response.ReasonPhrase} for delivery " +
                $"of snapshots {payload.FromSnapshot}..{payload.ToSnapshot}.");
        }
    }

    /// <summary>Whether a failing subscription is still inside its exponential backoff window.</summary>
    private static bool InBackoff(ChangeSubscription subscription, CdcOptions settings, DateTimeOffset now)
    {
        if (subscription.ConsecutiveFailures == 0 || subscription.LastAttemptUtc is null)
        {
            return false;
        }

        // Doubles per failure from the poll interval up to the cap: 15s, 30s, 1m, … A dead endpoint
        // settles at one attempt per cap interval instead of hammering on every poll.
        var exponent = Math.Min(subscription.ConsecutiveFailures, 12);
        var backoffTicks = settings.PollInterval.Ticks * (1L << exponent);
        var backoff = TimeSpan.FromTicks(Math.Min(backoffTicks, settings.MaxBackoff.Ticks));

        return subscription.LastAttemptUtc.Value + backoff > now;
    }

    private static string ToWireName(ChangeType change) => change switch
    {
        ChangeType.Insert => "insert",
        ChangeType.Delete => "delete",
        ChangeType.UpdatePreimage => "update_preimage",
        ChangeType.UpdatePostimage => "update_postimage",
        _ => "unknown",
    };

    private static string Truncate(string message)
        => message.Length <= 4000 ? message : message[..4000];
}

internal static partial class CdcLog
{
    [LoggerMessage(
        EventId = 3000,
        Level = LogLevel.Information,
        Message = "Delivered subscription {SubscriptionId} for catalog {Catalog}: snapshots {From}..{To}, {Changes} change(s), truncated={Truncated}")]
    public static partial void Delivered(
        ILogger logger, int subscriptionId, string catalog, long from, long to, int changes, bool truncated);

    [LoggerMessage(
        EventId = 3001,
        Level = LogLevel.Warning,
        Message = "Delivery failed for subscription {SubscriptionId} on catalog {Catalog}")]
    public static partial void DeliveryFailed(ILogger logger, Exception exception, int subscriptionId, string catalog);

    [LoggerMessage(
        EventId = 3002,
        Level = LogLevel.Error,
        Message = "Change-feed sweep failed")]
    public static partial void SweepFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 3003,
        Level = LogLevel.Information,
        Message = "Change-feed dispatcher polling every {PollInterval}")]
    public static partial void DispatcherStarted(ILogger logger, TimeSpan pollInterval);

    [LoggerMessage(
        EventId = 3004,
        Level = LogLevel.Information,
        Message = "Change-feed dispatcher is disabled")]
    public static partial void DispatcherDisabled(ILogger logger);
}

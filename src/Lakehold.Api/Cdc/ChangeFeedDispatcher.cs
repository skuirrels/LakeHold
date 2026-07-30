using System.Text;
using System.Text.Json;
using Lakehold.ControlPlane.Data;
using Lakehold.ControlPlane.Model;
using Lakehold.Engine.Catalog;
using Lakehold.Engine.Telemetry;
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

/// <summary>The body of one webhook delivery: a catalog's changes in one snapshot.</summary>
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
///         A durable PostgreSQL delivery row gives every subscription/snapshot pair one identity,
///         one signed body, and a bounded lease. Multiple API nodes may run the dispatcher; only the
///         node that claims the current lease posts it, and a crash permits another node to replay
///         the same body and delivery id after expiry.
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
    private readonly string _workerId = Guid.NewGuid().ToString("N");

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
        var subscriptionIds = await db.ChangeSubscriptions
            .AsNoTracking()
            .Where(s => s.Active)
            .OrderBy(s => s.Id)
            .Select(s => s.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        await Parallel.ForEachAsync(
            subscriptionIds,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = Math.Max(1, options.Value.MaxConcurrentSubscriptions),
            },
            async (subscriptionId, token) =>
            {
                LakeholdTelemetry.CdcWorkersActive.Add(1);
                try
                {
                    for (var advanced = 0;
                         advanced < options.Value.MaxSnapshotsPerSubscriptionPerSweep
                         && await SweepSubscriptionAsync(subscriptionId, token).ConfigureAwait(false);
                         advanced++)
                    {
                        // Continue in snapshot order until caught up, blocked, or this sweep's
                        // fairness ceiling is reached.
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    CdcLog.SubscriptionSweepFailed(logger, ex, subscriptionId);
                }
                finally
                {
                    LakeholdTelemetry.CdcWorkersActive.Add(-1);
                }
            })
            .ConfigureAwait(false);
    }

    private async Task<bool> SweepSubscriptionAsync(int subscriptionId, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneContext>();
        var lakehouse = scope.ServiceProvider.GetRequiredService<LakehouseService>();
        var settings = options.Value;
        var now = DateTimeOffset.UtcNow;

        var subscription = await db.ChangeSubscriptions
            .Include(s => s.Tenant)
            .FirstOrDefaultAsync(s => s.Id == subscriptionId && s.Active, cancellationToken)
            .ConfigureAwait(false);
        if (subscription is null)
        {
            return false;
        }

        var latest = await lakehouse
            .GetLatestSnapshotAsync(subscription.Tenant.Slug, subscription.CatalogName, cancellationToken)
            .ConfigureAwait(false);
        if (latest is null || latest.Value <= subscription.LastDeliveredSnapshot)
        {
            return false;
        }
        LakeholdTelemetry.CdcSnapshotLag.Record(latest.Value - subscription.LastDeliveredSnapshot);

        var snapshotId = subscription.LastDeliveredSnapshot + 1;
        var snapshot = await lakehouse
            .GetSnapshotAsync(subscription.Tenant.Slug, subscription.CatalogName, snapshotId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Snapshot {snapshotId} is no longer retained for subscription {subscription.Id}. "
                + "Re-bootstrap or replay from an available snapshot before advancing its cursor.");

        var delivery = await GetOrCreateDeliveryAsync(
                db,
                subscription,
                snapshot.SnapshotId,
                now,
                cancellationToken)
            .ConfigureAwait(false);

        if (delivery.DeliveredUtc is not null
            || delivery.NextAttemptUtc > now
            || (delivery.LeaseExpiresUtc > now
                && !string.Equals(delivery.LeaseOwner, _workerId, StringComparison.Ordinal)))
        {
            return false;
        }

        if (delivery.LeaseExpiresUtc <= now
            && delivery.LeaseOwner is not null
            && !string.Equals(delivery.LeaseOwner, _workerId, StringComparison.Ordinal))
        {
            LakeholdTelemetry.CdcLeaseTakeovers.Add(1);
        }

        delivery.LeaseOwner = _workerId;
        delivery.LeaseExpiresUtc = now + settings.LeaseDuration;
        delivery.Version++;
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another node claimed the same durable row from the version we read.
            LakeholdTelemetry.CdcLeaseConflicts.Add(1);
            return false;
        }

        try
        {
            await DeliverSnapshotAsync(
                    db,
                    lakehouse,
                    subscription,
                    delivery,
                    settings,
                    cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (
            ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            var failedAt = DateTimeOffset.UtcNow;
            delivery.AttemptCount++;
            delivery.LastAttemptUtc = failedAt;
            delivery.NextAttemptUtc = failedAt + BackoffFor(delivery.AttemptCount, settings);
            delivery.LeaseOwner = null;
            delivery.LeaseExpiresUtc = null;
            delivery.LastError = Truncate(ex.Message);
            delivery.Version++;

            subscription.ConsecutiveFailures++;
            subscription.LastAttemptUtc = failedAt;
            subscription.LastError = delivery.LastError;
            LakeholdTelemetry.CdcDeliveryAttempts.Add(
                1,
                new KeyValuePair<string, object?>(
                    LakeholdTelemetry.OutcomeKey,
                    LakeholdTelemetry.OutcomeError));
            CdcLog.DeliveryFailed(logger, ex, subscription.Id, subscription.CatalogName);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }
    }

    private static async Task<ChangeDelivery> GetOrCreateDeliveryAsync(
        ControlPlaneContext db,
        ChangeSubscription subscription,
        long snapshotId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var existing = await db.ChangeDeliveries
            .SingleOrDefaultAsync(
                d => d.SubscriptionId == subscription.Id && d.SnapshotId == snapshotId,
                cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            return existing;
        }

        var created = new ChangeDelivery
        {
            SubscriptionId = subscription.Id,
            DeliveryId = Guid.NewGuid().ToString("N"),
            SnapshotId = snapshotId,
            CreatedUtc = now,
        };
        db.ChangeDeliveries.Add(created);

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return created;
        }
        catch (DbUpdateException)
        {
            // A second node may have inserted the unique subscription/snapshot row first.
            db.Entry(created).State = EntityState.Detached;
            return await db.ChangeDeliveries
                .SingleAsync(
                    d => d.SubscriptionId == subscription.Id && d.SnapshotId == snapshotId,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>Delivers exactly one source snapshot and completes its durable outbox row.</summary>
    private async Task DeliverSnapshotAsync(
        ControlPlaneContext db,
        LakehouseService lakehouse,
        ChangeSubscription subscription,
        ChangeDelivery delivery,
        CdcOptions settings,
        CancellationToken cancellationToken)
    {
        var tenant = subscription.Tenant.Slug;
        var catalog = subscription.CatalogName;
        var snapshotId = delivery.SnapshotId;

        var watched = subscription.TableName is { Length: > 0 }
            ? [(subscription.SchemaName, subscription.TableName)]
            : await lakehouse.ListChangeTablesAsync(tenant, catalog, cancellationToken).ConfigureAwait(false);

        var tables = new List<ChangeDeliveryTable>();
        foreach (var (schema, table) in watched)
        {
            var page = await lakehouse
                .GetChangesAsync(
                    tenant,
                    catalog,
                    schema,
                    table,
                    snapshotId,
                    snapshotId,
                    settings.MaxChangesPerTable,
                    cancellationToken)
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
            // The snapshot touched nothing this subscription watches. Complete the durable delivery
            // without an outbound request so the next poll can advance in order.
            CompleteDelivery(subscription, delivery, DateTimeOffset.UtcNow);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (delivery.Payload is null)
        {
            var payload = new ChangeDeliveryPayload(
                subscription.Id,
                catalog,
                snapshotId,
                snapshotId,
                delivery.CreatedUtc,
                tables);
            delivery.Payload = JsonSerializer.SerializeToUtf8Bytes(payload, PayloadJson);
            delivery.Version++;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        await PostAsync(subscription, delivery, settings, cancellationToken).ConfigureAwait(false);

        var completedAt = DateTimeOffset.UtcNow;
        CompleteDelivery(subscription, delivery, completedAt);
        subscription.ConsecutiveFailures = 0;
        subscription.LastError = null;
        subscription.LastAttemptUtc = completedAt;
        LakeholdTelemetry.CdcDeliveryAttempts.Add(
            1,
            new KeyValuePair<string, object?>(
                LakeholdTelemetry.OutcomeKey,
                LakeholdTelemetry.OutcomeSuccess));
        LakeholdTelemetry.CdcPayloadBytes.Record(delivery.Payload.Length);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var deliveredChanges = tables.Sum(t => t.Changes.Count);
        var anyTruncated = tables.Any(t => t.Truncated);
        if (anyTruncated)
        {
            LakeholdTelemetry.CdcPayloadsTruncated.Add(1);
        }
        CdcLog.Delivered(
            logger,
            subscription.Id,
            catalog,
            snapshotId,
            snapshotId,
            deliveredChanges,
            anyTruncated);
    }

    private async Task PostAsync(
        ChangeSubscription subscription,
        ChangeDelivery delivery,
        CdcOptions settings,
        CancellationToken cancellationToken)
    {
        var body = delivery.Payload
            ?? throw new InvalidOperationException($"Delivery {delivery.DeliveryId} has no persisted payload.");
        // The delivery id and body are stable for deduplication, while the attempt timestamp must be
        // fresh so a receiver can reject captured requests without making a long-lived retry
        // permanently unverifiable.
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var endpoint = new Uri(subscription.EndpointUrl, UriKind.Absolute);
        var destination = await WebhookDestinationPolicy
            .ResolveAsync(endpoint, settings, cancellationToken)
            .ConfigureAwait(false);
        if (destination.Error is not null)
        {
            throw new InvalidOperationException(destination.Error);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        if (destination.Address is not null)
        {
            request.Options.Set(WebhookConnection.ApprovedAddress, destination.Address);
        }
        request.Content = new ByteArrayContent(body);
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json")
        {
            CharSet = Encoding.UTF8.WebName,
        };

        // Every retry reuses the durable delivery id and body, but signs them with this attempt's
        // fresh timestamp.
        request.Headers.TryAddWithoutValidation(
            WebhookSigner.SignatureHeader,
            WebhookSigner.Compute(body, subscription.Secret, timestamp, delivery.DeliveryId));
        request.Headers.TryAddWithoutValidation(WebhookSigner.DeliveryHeader, delivery.DeliveryId);
        request.Headers.TryAddWithoutValidation(
            WebhookSigner.TimestampHeader,
            timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation(
            WebhookSigner.SignatureVersionHeader,
            WebhookSigner.SignatureVersion);

        var client = httpClientFactory.CreateClient(HttpClientName);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(settings.DeliveryTimeout);

        var startedAt = TimeProvider.System.GetTimestamp();
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, timeout.Token).ConfigureAwait(false);
        }
        finally
        {
            LakeholdTelemetry.CdcDeliveryDuration.Record(
                TimeProvider.System.GetElapsedTime(startedAt).TotalSeconds);
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                // The status line is the receiver's answer; the body is not read because an arbitrary
                // endpoint's response has no business flowing into logs or the subscription row.
                throw new HttpRequestException(
                    $"Endpoint returned {(int)response.StatusCode} {response.ReasonPhrase} for delivery " +
                    $"of snapshot {delivery.SnapshotId}.");
            }
        }
    }

    private static void CompleteDelivery(
        ChangeSubscription subscription,
        ChangeDelivery delivery,
        DateTimeOffset completedAt)
    {
        subscription.LastDeliveredSnapshot = delivery.SnapshotId;
        delivery.AttemptCount++;
        delivery.LastAttemptUtc = completedAt;
        delivery.NextAttemptUtc = null;
        delivery.LeaseOwner = null;
        delivery.LeaseExpiresUtc = null;
        delivery.DeliveredUtc = completedAt;
        delivery.LastError = null;
        delivery.Version++;
    }

    private static TimeSpan BackoffFor(int attemptCount, CdcOptions settings)
    {
        var exponent = Math.Min(attemptCount, 12);
        var backoffTicks = settings.PollInterval.Ticks * (1L << exponent);
        return TimeSpan.FromTicks(Math.Min(backoffTicks, settings.MaxBackoff.Ticks));
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

    [LoggerMessage(
        EventId = 3005,
        Level = LogLevel.Warning,
        Message = "Change-feed sweep failed for subscription {SubscriptionId}")]
    public static partial void SubscriptionSweepFailed(ILogger logger, Exception exception, int subscriptionId);
}

using System.Text.Json;
using DuckDB.EFCoreProvider.Extensions;
using Lakehold.Api.Cdc;
using Lakehold.ControlPlane.Data;
using Lakehold.ControlPlane.Model;
using Lakehold.Engine.Catalog;
using Lakehold.Engine.Configuration;
using Lakehold.Engine.Execution;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Lakehold.Api.Tests;

/// <summary>
///     End-to-end cover for the dispatcher against a real control plane and a real DuckLake catalog,
///     with only the HTTP hop stubbed: that deliveries are signed and complete, that the cursor
///     advances exactly to what was delivered, that quiet snapshots advance without posting, and
///     that a failing endpoint backs off without blocking recovery.
/// </summary>
public sealed class ChangeFeedDispatcherTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lakehold-tests", Guid.NewGuid().ToString("N"));
    private ServiceProvider _services = null!;
    private CapturingHandler _handler = null!;
    private CdcOptions _cdcOptions = null!;
    private CatalogDescriptor _catalog = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);

        var lakehouseOptions = new LakehouseOptions
        {
            DataRoot = Path.Combine(_root, "data"),
            BackupRoot = Path.Combine(_root, "backups"),
            EjectRoot = Path.Combine(_root, "ejects"),
        };

        _catalog = new CatalogDescriptor(
            "cdclake",
            CatalogMetadataKind.LocalFile,
            Path.Combine(_root, "cdc.ducklake"),
            Path.Combine(_root, "data"));
        Directory.CreateDirectory(_catalog.DataPath);

        _cdcOptions = new CdcOptions
        {
            MaxChangesPerTable = 100,
            AllowHttp = true,
            AllowUnsafeDestinations = true,
        };
        _handler = new CapturingHandler();

        // The same graph Program.cs builds, minus hosting: a real control-plane database, a real
        // session pool, and the real service — only the webhook's HTTP hop is a stub.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<ControlPlaneContext>(o =>
            o.UseDuckDB($"Data Source={Path.Combine(_root, "controlplane.duckdb")}"));
        services.AddSingleton(Options.Create(lakehouseOptions));
        services.AddSingleton<DucklingPool>();
        services.AddSingleton<CatalogCache>();
        services.AddScoped<LakehouseService>();
        services.AddSingleton<IHttpClientFactory>(new StubHttpClientFactory(_handler));
        _services = services.BuildServiceProvider();

        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ControlPlaneContext>();
            await db.Database.EnsureCreatedAsync();

            var tenant = new Tenant { Slug = "acme", DisplayName = "Acme", CreatedUtc = DateTimeOffset.UtcNow };
            tenant.Catalogs.Add(new LakeCatalog
            {
                Name = _catalog.CatalogName,
                MetadataKind = _catalog.MetadataKind,
                MetadataSource = _catalog.MetadataSource,
                DataPath = _catalog.DataPath,
                CreatedUtc = DateTimeOffset.UtcNow,
            });
            db.Tenants.Add(tenant);
            await db.SaveChangesAsync();
        }

        // Give the catalog a table and a first batch of committed changes.
        var pool = _services.GetRequiredService<DucklingPool>();
        var duckling = await pool.GetOrStartAsync(_catalog, configure: null, CancellationToken.None);
        await duckling.ExecuteQueryAsync("CREATE TABLE orders (id BIGINT, status VARCHAR)", CancellationToken.None);
        await duckling.ExecuteQueryAsync("INSERT INTO orders VALUES (1,'new'), (2,'new')", CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Temp cleanup failing must not fail the test run.
        }
    }

    [Fact]
    public async Task Sweep_delivers_a_signed_payload_and_advances_the_cursor()
    {
        var id = await AddSubscriptionAsync(lastDelivered: 0, secret: "a-signing-secret-of-adequate-length");

        await NewDispatcher().SweepAsync(CancellationToken.None);

        var delivery = Assert.Single(_handler.Deliveries);
        Assert.Equal("http://cdc.test/hook", delivery.Url);

        // The signature must verify over the exact bytes that were posted.
        Assert.True(long.TryParse(delivery.Timestamp, out var timestamp));
        Assert.True(WebhookSigner.Verify(
            delivery.Body,
            "a-signing-secret-of-adequate-length",
            delivery.Signature,
            delivery.SignatureVersion,
            timestamp,
            delivery.DeliveryId,
            TimeProvider.System,
            TimeSpan.FromMinutes(5)));
        Assert.False(string.IsNullOrEmpty(delivery.DeliveryId));

        using var payload = JsonDocument.Parse(delivery.Body);
        var rootElement = payload.RootElement;
        Assert.Equal("cdclake", rootElement.GetProperty("catalog").GetString());
        Assert.Equal(
            rootElement.GetProperty("toSnapshot").GetInt64(),
            rootElement.GetProperty("fromSnapshot").GetInt64());

        var tables = rootElement.GetProperty("tables");
        Assert.Equal(1, tables.GetArrayLength());
        var changes = tables[0].GetProperty("changes");
        Assert.Equal(2, changes.GetArrayLength());
        Assert.Equal("insert", changes[0].GetProperty("changeType").GetString());

        // Cursor advanced to the delivered snapshot, so a second sweep has nothing to send.
        var toSnapshot = rootElement.GetProperty("toSnapshot").GetInt64();
        Assert.Equal(toSnapshot, await ReadCursorAsync(id));

        await NewDispatcher().SweepAsync(CancellationToken.None);
        Assert.Single(_handler.Deliveries);
    }

    [Fact]
    public async Task Sweep_advances_without_posting_when_snapshots_touch_nothing_watched()
    {
        // Subscribe to a table that exists but is never written; commit changes to another table.
        var pool = _services.GetRequiredService<DucklingPool>();
        var duckling = await pool.GetOrStartAsync(_catalog, configure: null, CancellationToken.None);
        await duckling.ExecuteQueryAsync("CREATE TABLE audit (note VARCHAR)", CancellationToken.None);

        var latest = await LatestSnapshotAsync();
        var id = await AddSubscriptionAsync(lastDelivered: latest, secret: "a-signing-secret-of-adequate-length", table: "audit");

        await duckling.ExecuteQueryAsync("INSERT INTO orders VALUES (9,'new')", CancellationToken.None);

        await NewDispatcher().SweepAsync(CancellationToken.None);

        // No delivery — but the cursor still advanced, or every later sweep would rescan the same
        // empty window forever.
        Assert.Empty(_handler.Deliveries);
        Assert.Equal(await LatestSnapshotAsync(), await ReadCursorAsync(id));
    }

    [Fact]
    public async Task Failed_delivery_keeps_identity_and_retries_with_a_fresh_verifiable_timestamp()
    {
        var id = await AddSubscriptionAsync(lastDelivered: 0, secret: "a-signing-secret-of-adequate-length");
        _handler.RespondWith = System.Net.HttpStatusCode.InternalServerError;

        await NewDispatcher().SweepAsync(CancellationToken.None);

        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ControlPlaneContext>();
            var subscription = await db.ChangeSubscriptions.SingleAsync(s => s.Id == id);
            using var failedPayload = JsonDocument.Parse(Assert.Single(_handler.Deliveries).Body);
            var failedSnapshot = failedPayload.RootElement.GetProperty("fromSnapshot").GetInt64();

            // At-least-once: the failed window stays owed.
            Assert.Equal(failedSnapshot - 1, subscription.LastDeliveredSnapshot);
            Assert.Equal(1, subscription.ConsecutiveFailures);
            Assert.Contains("500", subscription.LastError, StringComparison.Ordinal);
        }

        // The immediate next sweep is inside the backoff window and must not attempt again.
        var attemptsAfterFailure = _handler.Deliveries.Count;
        await NewDispatcher().SweepAsync(CancellationToken.None);
        Assert.Equal(attemptsAfterFailure, _handler.Deliveries.Count);

        // Once the endpoint recovers and backoff lapses, the same window is redelivered in full.
        _handler.RespondWith = System.Net.HttpStatusCode.OK;
        await AgeDeliveryAsync(id, TimeSpan.FromHours(1));
        await ClearBackoffAsync(id);
        await NewDispatcher().SweepAsync(CancellationToken.None);

        Assert.Equal(await LatestSnapshotAsync(), await ReadCursorAsync(id));
        Assert.Equal(2, _handler.Deliveries.Count);
        Assert.Equal(_handler.Deliveries[0].DeliveryId, _handler.Deliveries[1].DeliveryId);
        Assert.Equal(_handler.Deliveries[0].Body, _handler.Deliveries[1].Body);
        var retry = _handler.Deliveries[1];
        Assert.True(long.TryParse(retry.Timestamp, out var retryTimestamp));
        Assert.True(WebhookSigner.Verify(
            retry.Body,
            "a-signing-secret-of-adequate-length",
            retry.Signature,
            retry.SignatureVersion,
            retryTimestamp,
            retry.DeliveryId,
            TimeProvider.System,
            TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public async Task Delivery_timeout_is_recorded_without_stopping_the_dispatcher()
    {
        var id = await AddSubscriptionAsync(lastDelivered: 0, secret: "a-signing-secret-of-adequate-length");
        _cdcOptions.DeliveryTimeout = TimeSpan.FromMilliseconds(20);
        _handler.Delay = TimeSpan.FromSeconds(1);

        // A per-request timeout is not application shutdown. The sweep must return normally and
        // release the lease so this and every other subscription remain serviceable.
        await NewDispatcher().SweepAsync(CancellationToken.None);

        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ControlPlaneContext>();
            var subscription = await db.ChangeSubscriptions.AsNoTracking().SingleAsync(s => s.Id == id);
            var delivery = await db.ChangeDeliveries
                .AsNoTracking()
                .SingleAsync(d => d.SubscriptionId == id && d.DeliveredUtc == null);
            Assert.Equal(delivery.SnapshotId - 1, subscription.LastDeliveredSnapshot);
            Assert.Equal(1, subscription.ConsecutiveFailures);
            Assert.Null(delivery.LeaseOwner);
            Assert.Null(delivery.LeaseExpiresUtc);
            Assert.NotNull(delivery.NextAttemptUtc);
        }

        _handler.Delay = TimeSpan.Zero;
        await ClearBackoffAsync(id);
        await NewDispatcher().SweepAsync(CancellationToken.None);
        Assert.Equal(await LatestSnapshotAsync(), await ReadCursorAsync(id));
    }

    [Fact]
    public async Task Safe_destination_resolution_is_pinned_on_the_http_request()
    {
        _cdcOptions.AllowUnsafeDestinations = false;
        var id = await AddSubscriptionAsync(
            lastDelivered: 0,
            secret: "a-signing-secret-of-adequate-length",
            endpointUrl: "http://93.184.216.34/hook");

        await NewDispatcher().SweepAsync(CancellationToken.None);

        Assert.Equal(await LatestSnapshotAsync(), await ReadCursorAsync(id));
        Assert.Equal("93.184.216.34", Assert.Single(_handler.Deliveries).ApprovedAddress);
    }

    [Fact]
    public async Task Concurrent_dispatchers_claim_one_durable_delivery()
    {
        var id = await AddSubscriptionAsync(lastDelivered: 0, secret: "a-signing-secret-of-adequate-length");
        _handler.Delay = TimeSpan.FromMilliseconds(100);

        // Model two API nodes polling the shared control plane at the same time. The durable
        // subscription/snapshot row and its optimistic version allow exactly one node to own the
        // live lease; the other stands down without posting a second request.
        await Task.WhenAll(
            NewDispatcher().SweepAsync(CancellationToken.None),
            NewDispatcher().SweepAsync(CancellationToken.None));

        Assert.Single(_handler.Deliveries);
        Assert.Equal(await LatestSnapshotAsync(), await ReadCursorAsync(id));

        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneContext>();
        var deliveries = await db.ChangeDeliveries
            .AsNoTracking()
            .Where(delivery => delivery.SubscriptionId == id)
            .ToListAsync();
        Assert.All(deliveries, delivery => Assert.NotNull(delivery.DeliveredUtc));
    }

    [Fact]
    public async Task Oversized_windows_are_delivered_truncated_and_flagged()
    {
        _cdcOptions.MaxChangesPerTable = 3;
        var id = await AddSubscriptionAsync(lastDelivered: 0, secret: "a-signing-secret-of-adequate-length");

        var pool = _services.GetRequiredService<DucklingPool>();
        var duckling = await pool.GetOrStartAsync(_catalog, configure: null, CancellationToken.None);
        await duckling.ExecuteQueryAsync(
            "INSERT INTO orders SELECT i, 'bulk' FROM range(100, 150) t(i)", CancellationToken.None);

        await NewDispatcher().SweepAsync(CancellationToken.None);

        Assert.Equal(2, _handler.Deliveries.Count);
        var delivery = _handler.Deliveries[^1];
        using var payload = JsonDocument.Parse(delivery.Body);
        var table = payload.RootElement.GetProperty("tables")[0];

        Assert.True(table.GetProperty("truncated").GetBoolean());
        Assert.Equal(3, table.GetProperty("changes").GetArrayLength());

        // The cursor still advances: the receiver has been told the full range it is responsible
        // for, and the pull API covers the remainder.
        Assert.Equal(await LatestSnapshotAsync(), await ReadCursorAsync(id));
    }

    private ChangeFeedDispatcher NewDispatcher() => new(
        _services.GetRequiredService<IServiceScopeFactory>(),
        _services.GetRequiredService<IHttpClientFactory>(),
        Options.Create(_cdcOptions),
        NullLogger<ChangeFeedDispatcher>.Instance);

    private async Task<int> AddSubscriptionAsync(
        long lastDelivered,
        string secret,
        string? table = null,
        string endpointUrl = "http://cdc.test/hook")
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneContext>();
        var tenant = await db.Tenants.SingleAsync(t => t.Slug == "acme");

        var subscription = new ChangeSubscription
        {
            TenantId = tenant.Id,
            CatalogName = _catalog.CatalogName,
            TableName = table,
            EndpointUrl = endpointUrl,
            Secret = secret,
            LastDeliveredSnapshot = lastDelivered,
            CreatedUtc = DateTimeOffset.UtcNow,
        };
        db.ChangeSubscriptions.Add(subscription);
        await db.SaveChangesAsync();
        return subscription.Id;
    }

    private async Task<long> ReadCursorAsync(int id)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneContext>();
        return (await db.ChangeSubscriptions.AsNoTracking().SingleAsync(s => s.Id == id)).LastDeliveredSnapshot;
    }

    private async Task ClearBackoffAsync(int id)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneContext>();
        var subscription = await db.ChangeSubscriptions.SingleAsync(s => s.Id == id);
        subscription.LastAttemptUtc = DateTimeOffset.UtcNow - TimeSpan.FromHours(2);
        var delivery = await db.ChangeDeliveries.SingleAsync(d => d.SubscriptionId == id && d.DeliveredUtc == null);
        delivery.NextAttemptUtc = DateTimeOffset.UtcNow - TimeSpan.FromHours(2);
        await db.SaveChangesAsync();
    }

    private async Task AgeDeliveryAsync(int id, TimeSpan age)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneContext>();
        var delivery = await db.ChangeDeliveries
            .SingleAsync(d => d.SubscriptionId == id && d.DeliveredUtc == null);
        delivery.CreatedUtc = DateTimeOffset.UtcNow - age;
        await db.SaveChangesAsync();
    }

    private async Task<long> LatestSnapshotAsync()
    {
        await using var scope = _services.CreateAsyncScope();
        var lakehouse = scope.ServiceProvider.GetRequiredService<LakehouseService>();
        return (await lakehouse.GetLatestSnapshotAsync("acme", _catalog.CatalogName, CancellationToken.None))!.Value;
    }

    /// <summary>One captured webhook post.</summary>
    private sealed record Delivery(
        string Url,
        byte[] Body,
        string? Signature,
        string? DeliveryId,
        string? Timestamp,
        string? SignatureVersion,
        string? ApprovedAddress);

    /// <summary>Captures outbound deliveries and answers with a configurable status.</summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly object _gate = new();
        private readonly List<Delivery> _deliveries = [];

        public IReadOnlyList<Delivery> Deliveries
        {
            get
            {
                lock (_gate)
                {
                    return [.. _deliveries];
                }
            }
        }

        public System.Net.HttpStatusCode RespondWith { get; set; } = System.Net.HttpStatusCode.OK;

        public TimeSpan Delay { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, cancellationToken);
            }

            var body = request.Content is null
                ? []
                : await request.Content.ReadAsByteArrayAsync(cancellationToken);

            request.Headers.TryGetValues(WebhookSigner.SignatureHeader, out var signatures);
            request.Headers.TryGetValues(WebhookSigner.DeliveryHeader, out var deliveryIds);
            request.Headers.TryGetValues(WebhookSigner.TimestampHeader, out var timestamps);
            request.Headers.TryGetValues(WebhookSigner.SignatureVersionHeader, out var versions);
            request.Options.TryGetValue(WebhookConnection.ApprovedAddress, out var approvedAddress);

            lock (_gate)
            {
                _deliveries.Add(new Delivery(
                    request.RequestUri!.ToString(),
                    body,
                    signatures?.FirstOrDefault(),
                    deliveryIds?.FirstOrDefault(),
                    timestamps?.FirstOrDefault(),
                    versions?.FirstOrDefault(),
                    approvedAddress?.ToString()));
            }

            return new HttpResponseMessage(RespondWith);
        }
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}

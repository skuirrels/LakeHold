using System.Diagnostics;
using System.Diagnostics.Metrics;
using Lakehold.Engine.Telemetry;
using Xunit;

namespace Lakehold.Api.Tests;

/// <summary>
///     Cover for the telemetry surface: that the source and meter actually emit under a listener,
///     and that nothing carries a tenant's SQL or a credential-bearing value.
/// </summary>
/// <remarks>
///     Instrumentation fails silently — a span nobody records and a counter nobody increments look
///     exactly like a quiet system. These assert the wiring end to end rather than trusting it.
/// </remarks>
public sealed class TelemetryTests
{
    [Fact]
    public void The_activity_source_emits_when_a_listener_is_attached()
    {
        var captured = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == LakeholdTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = captured.Add,
        };
        ActivitySource.AddActivityListener(listener);

        using (var activity = LakeholdTelemetry.Source.StartActivity("lakehold.query"))
        {
            activity?.SetTag(LakeholdTelemetry.TenantKey, "demo");
            activity?.SetTag(LakeholdTelemetry.CatalogKey, "analytics");
        }

        var span = Assert.Single(captured);
        Assert.Equal("lakehold.query", span.OperationName);
        Assert.Equal("demo", span.GetTagItem(LakeholdTelemetry.TenantKey));
        Assert.Equal("analytics", span.GetTagItem(LakeholdTelemetry.CatalogKey));
    }

    [Fact]
    public void Instruments_report_through_the_registered_meter()
    {
        var measurements = new List<(string Instrument, double Value)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == LakeholdTelemetry.MeterName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };

        listener.SetMeasurementEventCallback<double>(
            (instrument, value, _, _) => measurements.Add((instrument.Name, value)));
        listener.SetMeasurementEventCallback<long>(
            (instrument, value, _, _) => measurements.Add((instrument.Name, value)));
        listener.Start();

        LakeholdTelemetry.QueryDuration.Record(0.25);
        LakeholdTelemetry.SessionQueueDuration.Record(0.01);
        LakeholdTelemetry.WarmSessions.Add(1);
        LakeholdTelemetry.CatalogCacheRequests.Add(1);

        listener.Dispose();

        Assert.Contains(measurements, m => m.Instrument == "lakehold.query.duration" && m.Value == 0.25);
        Assert.Contains(measurements, m => m.Instrument == "lakehold.session.queue.duration");
        Assert.Contains(measurements, m => m.Instrument == "lakehold.sessions.warm");
        Assert.Contains(measurements, m => m.Instrument == "lakehold.catalog.cache.requests");
    }

    [Fact]
    public void The_catalog_cache_reports_hits_and_misses()
    {
        var byResult = new Dictionary<string, long>(StringComparer.Ordinal);
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name == "lakehold.catalog.cache.requests")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };

        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == LakeholdTelemetry.ResultKey && tag.Value is string result)
                {
                    byResult[result] = byResult.GetValueOrDefault(result) + value;
                }
            }
        });
        listener.Start();

        var cache = new ControlPlane.Data.CatalogCache();
        cache.TryGet("demo", "analytics", out _);
        cache.Set("demo", "analytics", new ControlPlane.Data.ResolvedCatalog(
            new Engine.Catalog.CatalogDescriptor(
                "analytics", Engine.Catalog.CatalogMetadataKind.LocalFile, "/tmp/a.ducklake", "/tmp/data"),
            TenantId: 1));
        cache.TryGet("demo", "analytics", out _);

        listener.Dispose();

        Assert.Equal(1, byResult.GetValueOrDefault(LakeholdTelemetry.ResultMiss));
        Assert.Equal(1, byResult.GetValueOrDefault(LakeholdTelemetry.ResultHit));
    }

    [Fact]
    public void No_tag_key_invites_recording_submitted_sql_or_a_credential()
    {
        // A tenant's statement is their data and a metadata source can be a connection string, so
        // neither may become a span attribute or metric tag. Guarding the vocabulary keeps a future
        // "just add the query text for debugging" from being a one-word change.
        var keys = typeof(LakeholdTelemetry)
            .GetFields()
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToArray();

        Assert.DoesNotContain(keys, k => k.Contains("sql", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(keys, k => k.Contains("statement", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(keys, k => k.Contains("secret", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(keys, k => k.Contains("credential", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(keys, k => k.Contains("password", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(keys, k => k.Contains("connection", StringComparison.OrdinalIgnoreCase));
    }
}

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.ServiceDiscovery;
using OpenTelemetry;
using OpenTelemetry.Instrumentation.EntityFrameworkCore;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Microsoft.Extensions.Hosting;

// Adds common Aspire services: service discovery, resilience, health checks, and OpenTelemetry.
// This project should be referenced by each service project in your solution.
// To learn more about using this project, see https://aka.ms/aspire/service-defaults
public static class Extensions
{
    private const string HealthEndpointPath = "/health";
    private const string AlivenessEndpointPath = "/alive";

    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.ConfigureOpenTelemetry();

        builder.AddDefaultHealthChecks();

        builder.Services.AddServiceDiscovery();

        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            // Turn on resilience by default
            http.AddStandardResilienceHandler();

            // Turn on service discovery by default
            http.AddServiceDiscovery();
        });

        // Uncomment the following to restrict the allowed schemes for service discovery.
        // builder.Services.Configure<ServiceDiscoveryOptions>(options =>
        // {
        //     options.AllowedSchemes = ["https"];
        // });

        return builder;
    }

    public static TBuilder ConfigureOpenTelemetry<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics =>
            {
                metrics.AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation();
            })
            .WithTracing(tracing =>
            {
                tracing.AddSource(builder.Environment.ApplicationName)
                    .AddAspNetCoreInstrumentation(tracing =>
                        // Exclude health check requests from tracing
                        tracing.Filter = context =>
                            !context.Request.Path.StartsWithSegments(HealthEndpointPath)
                            && !context.Request.Path.StartsWithSegments(AlivenessEndpointPath)
                    )
                    // Uncomment the following line to enable gRPC instrumentation (requires the OpenTelemetry.Instrumentation.GrpcNetClient package)
                    //.AddGrpcClientInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddEntityFrameworkCoreInstrumentation(ConfigureEntityFrameworkInstrumentation);
            });

        builder.AddOpenTelemetryExporters();

        return builder;
    }

    /// <summary>
    ///     Configures EF Core instrumentation so it can never export a submitted statement.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The data plane is EF Core too. A tenant's statement reaches DuckDB through
    ///         <c>LakeContext.Database.SqlQueryDynamicRawAsync</c>, so whatever this instrumentation
    ///         captures as command text <em>is</em> tenant data — not just the control plane's own
    ///         generated SQL. That makes this the one instrumentation on the node that could quietly
    ///         export the thing the logging rules exist to keep out of telemetry.
    ///     </para>
    ///     <para>
    ///         Verified on 1.17.0-beta.1: this version exports no command text at all — only
    ///         <c>ef.provider</c>, <c>db.system</c>, <c>peer.service</c>, and <c>db.name</c> — so
    ///         nothing leaks today and the strip below is defence in depth, not a fix. It is kept
    ///         because the package removed the switch that used to govern this
    ///         (<c>SetDbStatementForText</c>) rather than settling it, this is a prerelease whose
    ///         defaults can move, and the failure would be silent: statements would simply start
    ///         appearing in whatever collector is configured. Clearing the tags costs one delegate
    ///         call per command and covers both the old (<c>db.statement</c>) and new
    ///         (<c>db.query.text</c>) conventions.
    ///     </para>
    ///     <para>
    ///         Public so the tests can assert against the exact configuration production runs rather
    ///         than a copy of it — see <c>EfInstrumentationTests</c>.
    ///     </para>
    /// </remarks>
    public static void ConfigureEntityFrameworkInstrumentation(EntityFrameworkInstrumentationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.EnrichWithIDbCommand = static (activity, _) =>
        {
            // Setting a tag to null removes it, so this strips whatever the instrumentation set.
            activity.SetTag("db.statement", null);
            activity.SetTag("db.query.text", null);
        };
    }

    private static TBuilder AddOpenTelemetryExporters<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        var useOtlpExporter = !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);

        if (useOtlpExporter)
        {
            builder.Services.AddOpenTelemetry().UseOtlpExporter();
        }

        // Uncomment the following lines to enable the Azure Monitor exporter (requires the Azure.Monitor.OpenTelemetry.AspNetCore package)
        //if (!string.IsNullOrEmpty(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
        //{
        //    builder.Services.AddOpenTelemetry()
        //       .UseAzureMonitor();
        //}

        return builder;
    }

    public static TBuilder AddDefaultHealthChecks<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Services.AddHealthChecks()
            // Liveness only, and deliberately trivial: it answers "is this process still able to
            // serve a request", nothing more. Anything tagged "live" gates restarts, so a dependency
            // added here would turn an outage in that dependency into a restart loop across every
            // node. Readiness dependencies belong on untagged checks, which only /health runs.
            .AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);

        return builder;
    }

    /// <summary>
    ///     Maps the readiness and liveness endpoints in every environment.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         These are mapped in production deliberately: an orchestrator that cannot probe a node
    ///         cannot restart a wedged one or keep traffic off one that is still starting, and a
    ///         lakehouse node's start-up is not instant — it attaches catalogs and may seed. The
    ///         template gates them to development because exposing them has security implications, so
    ///         those are addressed here rather than by removing the check.
    ///     </para>
    ///     <para>
    ///         What is exposed is a bare <c>Healthy</c>/<c>Unhealthy</c> string. The response writer
    ///         is pinned to the terse one rather than left to the default, so no check name,
    ///         exception, duration, or dependency detail can reach an anonymous caller — that is the
    ///         information-disclosure half of the concern. The other half is that a probe endpoint is
    ///         cheap to hammer, so readiness must stay dependency-light; see
    ///         <see cref="AddDefaultHealthChecks"/>.
    ///     </para>
    ///     <para>
    ///         Neither path is authenticated, because a probe runs before and during auth failures.
    ///         Restrict them at the network edge — do not route <c>/health</c> or <c>/alive</c> from a
    ///         public ingress.
    ///     </para>
    /// </remarks>
    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // All health checks must pass for the app to be considered ready to accept traffic.
        app.MapHealthChecks(HealthEndpointPath, new HealthCheckOptions
        {
            ResponseWriter = WriteStatusOnlyAsync,
        });

        // Only checks tagged "live" must pass for the app to be considered alive. Liveness must not
        // depend on anything external: a failing dependency should stop traffic, not trigger a
        // restart loop that cannot fix it.
        app.MapHealthChecks(AlivenessEndpointPath, new HealthCheckOptions
        {
            Predicate = r => r.Tags.Contains("live"),
            ResponseWriter = WriteStatusOnlyAsync,
        });

        return app;
    }

    /// <summary>Writes the status and nothing else, so a probe reveals no internals.</summary>
    private static Task WriteStatusOnlyAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "text/plain";

        // A probe result is a point-in-time answer; a cached one is worse than none.
        context.Response.Headers.CacheControl = "no-store, no-cache";
        return context.Response.WriteAsync(report.Status.ToString());
    }
}

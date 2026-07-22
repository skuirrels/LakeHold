using System.Net;
using System.Net.Sockets;
using Lakehold.Engine.Telemetry;
using Microsoft.Extensions.Options;

namespace Lakehold.Api.PgWire;

/// <summary>Structured logging for the wire endpoint.</summary>
internal static partial class PgWireLog
{
    [LoggerMessage(Level = LogLevel.Information,
        Message = "PostgreSQL wire endpoint listening on port {Port}.")]
    public static partial void Listening(ILogger logger, int port);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Wire connection opened for tenant {Tenant}, catalog {Catalog}.")]
    public static partial void ConnectionOpened(ILogger logger, string tenant, string catalog);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Wire authentication failed for tenant {Tenant}.")]
    public static partial void AuthenticationFailed(ILogger logger, string tenant);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Wire connection ended.")]
    public static partial void ConnectionClosed(ILogger logger, Exception? exception);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Wire connection refused: {Active} connections already open.")]
    public static partial void ConnectionRefused(ILogger logger, int active);

    [LoggerMessage(Level = LogLevel.Error, Message = "Wire connection failed unexpectedly.")]
    public static partial void ConnectionFaulted(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "The PostgreSQL wire endpoint could not listen on port {Port}. The HTTP API is "
            + "unaffected, but no BI client can connect until this is resolved.")]
    public static partial void ListenFailed(ILogger logger, Exception exception, int port);
}

/// <summary>
///     Accepts PostgreSQL wire-protocol connections and serves each on its own task.
/// </summary>
/// <remarks>
///     Hosted beside the HTTP API rather than in front of it, in the same way the CDC dispatcher is:
///     it is a serving surface over the same engine, so it belongs in the API project and shares its
///     lifetime, configuration, and dependency injection.
/// </remarks>
internal sealed class PgWireServer(
    IOptions<PgWireOptions> options,
    IServiceScopeFactory scopeFactory,
    ILogger<PgWireServer> logger) : BackgroundService
{
    private readonly PgWireOptions _options = options.Value;
    private int _active;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var listener = new TcpListener(IPAddress.Any, _options.Port);

        try
        {
            listener.Start();
        }
        catch (SocketException ex)
        {
            // A port already in use must not take the HTTP API down with it. An unhandled exception
            // from a BackgroundService stops the host, which would mean an occupied BI port also
            // stopped the workbench, the API, and scheduled maintenance. Logged loudly and left
            // stopped instead: the operator asked for this endpoint, so silence would be worse than
            // the error, but so would collateral shutdown.
            PgWireLog.ListenFailed(logger, ex, _options.Port);
            return;
        }

        PgWireLog.Listening(logger, _options.Port);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stoppingToken).ConfigureAwait(false);

                // Refuse past the ceiling rather than queue: every accepted connection can hold a
                // statement on a session gate, and an unbounded accept loop turns a client's
                // connection-pool misconfiguration into this node's memory problem.
                if (Interlocked.Increment(ref _active) > _options.MaxConnections)
                {
                    Interlocked.Decrement(ref _active);
                    PgWireLog.ConnectionRefused(logger, _options.MaxConnections);
                    LakeholdTelemetry.WireConnectionsClosed.Add(
                        1, new KeyValuePair<string, object?>(LakeholdTelemetry.OutcomeKey, "refused"));
                    client.Dispose();
                    continue;
                }

                _ = ServeAsync(client, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
        finally
        {
            listener.Stop();
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken stoppingToken)
    {
        LakeholdTelemetry.WireConnections.Add(1);
        var outcome = "closed";

        try
        {
            using (client)
            {
                // Nagle batches small writes, and this protocol is a conversation of small writes:
                // a client waiting on ReadyForQuery would pay the delay on every round trip.
                client.NoDelay = true;

                await using var stream = client.GetStream();
                var connection = new PgWireConnection(stream, _options, scopeFactory, logger);
                await connection.RunAsync(stoppingToken).ConfigureAwait(false);
            }

            PgWireLog.ConnectionClosed(logger, null);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException)
        {
            // A BI tool closing a pooled connection mid-conversation is ordinary, not an incident.
            outcome = "dropped";
            PgWireLog.ConnectionClosed(logger, ex);
        }
        catch (Exception ex)
        {
            outcome = "faulted";
            // Anything else is a bug in the protocol handler. It must not escape into an unobserved
            // task, where it would surface as a connection that simply vanished with nothing in the
            // log to explain it — the hardest possible failure to diagnose from the client end.
            PgWireLog.ConnectionFaulted(logger, ex);
        }
        finally
        {
            Interlocked.Decrement(ref _active);
            LakeholdTelemetry.WireConnections.Add(-1);
            LakeholdTelemetry.WireConnectionsClosed.Add(
                1, new KeyValuePair<string, object?>(LakeholdTelemetry.OutcomeKey, outcome));
        }
    }
}

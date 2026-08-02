using Lakehold.ControlPlane.Data;
using Lakehold.ControlPlane.Model;
using Microsoft.Extensions.Options;

namespace Lakehold.Api.Connectors;

/// <summary>Polls durable schedules; PostgreSQL leases keep several API nodes from duplicating work.</summary>
internal sealed class ConnectorWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<ConnectorOptions> options,
    ILogger<ConnectorWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var workers = Enumerable.Range(0, options.Value.MaxConcurrentRuns)
                    .Select(_ => RunNextAsync(stoppingToken));
                await Task.WhenAll(workers).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                ConnectorWorkerLog.SweepFailed(logger, ex);
            }

            await Task.Delay(options.Value.PollInterval, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task RunNextAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var connectors = scope.ServiceProvider.GetRequiredService<DataConnectorService>();
        var runner = scope.ServiceProvider.GetRequiredService<ConnectorRunner>();
        while (!cancellationToken.IsCancellationRequested)
        {
            var id = await connectors.FindNextDueIdAsync(DateTimeOffset.UtcNow, cancellationToken)
                .ConfigureAwait(false);
            if (id is null)
            {
                return;
            }

            // Several workers may observe the same oldest due id. The database claim is atomic; a
            // loser immediately re-queries so it can service the next due connector in this sweep.
            if (await runner.RunAsync(id.Value, DataConnectorTrigger.Scheduled, cancellationToken)
                    .ConfigureAwait(false) is not null)
            {
                return;
            }
        }
    }
}

internal sealed partial class ConnectorWorkerLog
{
    [LoggerMessage(EventId = 4310, Level = LogLevel.Error, Message = "Managed connector schedule sweep failed")]
    public static partial void SweepFailed(ILogger logger, Exception exception);
}

namespace Lakehold.Api.PublicApi;

/// <summary>Bounds completed public-API coordination state without deleting indeterminate work.</summary>
public sealed partial class PublicApiRetentionWorker(
    IServiceScopeFactory scopes,
    TimeProvider clock,
    ILogger<PublicApiRetentionWorker> logger) : BackgroundService
{
    internal static readonly TimeSpan SweepInterval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var idempotency = scope.ServiceProvider.GetRequiredService<PublicApiIdempotencyStore>();
                var operations = scope.ServiceProvider.GetRequiredService<PublicApiOperationStore>();
                var deletedIdempotency = await idempotency.DeleteExpiredCompletedAsync(stoppingToken)
                    .ConfigureAwait(false);
                var deletedOperations = await operations.DeleteExpiredTerminalAsync(stoppingToken)
                    .ConfigureAwait(false);
                if (deletedIdempotency > 0 || deletedOperations > 0)
                {
                    LogDeleted(logger, deletedIdempotency, deletedOperations);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                LogFailure(logger, exception);
            }

            await Task.Delay(SweepInterval, clock, stoppingToken).ConfigureAwait(false);
        }
    }

    [LoggerMessage(
        EventId = 2354,
        Level = LogLevel.Information,
        Message = "Public API retention removed {IdempotencyRecords} completed idempotency records and {OperationRecords} terminal operation records.")]
    private static partial void LogDeleted(
        ILogger logger,
        int idempotencyRecords,
        int operationRecords);

    [LoggerMessage(
        EventId = 2355,
        Level = LogLevel.Error,
        Message = "Public API retention sweep failed.")]
    private static partial void LogFailure(ILogger logger, Exception exception);
}

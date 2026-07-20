using Lakehold.ControlPlane.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Quartz;

namespace Lakehold.Api.Scheduling;

/// <summary>Records what a scheduled run did, so the schedule is observable rather than assumed.</summary>
public sealed record ScheduledRun(
    string Job,
    string Tenant,
    string Catalog,
    DateTimeOffset StartedUtc,
    double ElapsedMilliseconds,
    bool Succeeded,
    string Detail);

/// <summary>
///     In-memory ring of recent scheduled runs, surfaced through the API.
/// </summary>
/// <remarks>
///     A scheduler you cannot observe is a scheduler you do not trust — and an operator who does not
///     trust the backup schedule will not rely on it. Deliberately in-memory and bounded: this is an
///     operational read-out, not an audit trail. Query history already covers durable auditing.
/// </remarks>
public sealed class ScheduledRunLog
{
    private const int Capacity = 100;
    private readonly Lock _sync = new();
    private readonly Queue<ScheduledRun> _runs = new(Capacity);

    public void Record(ScheduledRun run)
    {
        lock (_sync)
        {
            if (_runs.Count == Capacity)
            {
                _runs.Dequeue();
            }

            _runs.Enqueue(run);
        }
    }

    public IReadOnlyList<ScheduledRun> Recent()
    {
        lock (_sync)
        {
            return [.. _runs.Reverse()];
        }
    }
}

/// <summary>
///     Base for jobs that run one maintenance operation across every catalog.
/// </summary>
/// <remarks>
///     <see cref="DisallowConcurrentExecutionAttribute"/> matters more than it looks: maintenance
///     serialises on each tenant's session gate anyway, so overlapping fires would queue rather than
///     parallelise, and a slow catalog would accumulate a backlog of identical work.
/// </remarks>
[DisallowConcurrentExecution]
public abstract class MaintenanceJobBase(
    IServiceScopeFactory scopeFactory,
    ScheduledRunLog log,
    IOptions<MaintenanceScheduleOptions> options,
    ILogger logger) : IJob
{
    /// <summary>Maintenance operation name, as understood by <see cref="LakehouseService"/>.</summary>
    protected abstract string Operation { get; }

    public async Task Execute(IJobExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneContext>();
        var lakehouse = scope.ServiceProvider.GetRequiredService<LakehouseService>();

        var targets = await db.Catalogs
            .AsNoTracking()
            .Include(c => c.Tenant)
            .Where(c => !c.IsReadOnly)
            .Select(c => new { Tenant = c.Tenant.Slug, Catalog = c.Name })
            .ToListAsync(context.CancellationToken)
            .ConfigureAwait(false);

        foreach (var target in targets)
        {
            var startedAt = DateTimeOffset.UtcNow;
            var timestamp = TimeProvider.System.GetTimestamp();

            try
            {
                var settings = options.Value;
                var result = await lakehouse
                    .RunScheduledMaintenanceAsync(
                        target.Tenant, target.Catalog, Operation,
                        settings.NodeId, settings.LeaseDuration, context.CancellationToken)
                    .ConfigureAwait(false);

                // A null result means another node claimed this catalog. Recorded rather than
                // dropped: "nothing in the log" and "another node did it" look identical otherwise,
                // and an operator checking why a backup is missing needs to tell them apart.
                log.Record(new ScheduledRun(
                    Operation, target.Tenant, target.Catalog, startedAt,
                    TimeProvider.System.GetElapsedTime(timestamp).TotalMilliseconds, true,
                    result?.Detail ?? "skipped: another node holds the maintenance lease"));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One catalog failing must not abort the sweep — the next tenant's backup is
                // independent, and skipping it would silently widen everyone's exposure window.
                SchedulerLog.JobFailed(logger, ex, Operation, target.Tenant, target.Catalog);

                log.Record(new ScheduledRun(
                    Operation, target.Tenant, target.Catalog, startedAt,
                    TimeProvider.System.GetElapsedTime(timestamp).TotalMilliseconds, false, ex.Message));
            }
        }
    }
}

/// <summary>
///     Flushes inlined commits to Parquet.
/// </summary>
/// <remarks>
///     The most important job of the three. Inlined data that has never been flushed is the one
///     category that is <em>permanently</em> unrecoverable if the catalog is lost, so this interval
///     is the upper bound on unrecoverable data loss.
/// </remarks>
public sealed class FlushInlinedDataJob(
    IServiceScopeFactory scopeFactory,
    ScheduledRunLog log,
    IOptions<MaintenanceScheduleOptions> options,
    ILogger<FlushInlinedDataJob> logger) : MaintenanceJobBase(scopeFactory, log, options, logger)
{
    protected override string Operation => "flush";
}

/// <summary>Exports catalog metadata to Parquet and prunes old generations.</summary>
public sealed class CatalogBackupJob(
    IServiceScopeFactory scopeFactory,
    ScheduledRunLog log,
    IOptions<MaintenanceScheduleOptions> options,
    ILogger<CatalogBackupJob> logger) : MaintenanceJobBase(scopeFactory, log, options, logger)
{
    protected override string Operation => "backup";
}

/// <summary>Merges small Parquet files. Non-destructive, but I/O heavy, so it runs off-peak.</summary>
public sealed class CompactJob(
    IServiceScopeFactory scopeFactory,
    ScheduledRunLog log,
    IOptions<MaintenanceScheduleOptions> options,
    ILogger<CompactJob> logger) : MaintenanceJobBase(scopeFactory, log, options, logger)
{
    protected override string Operation => "compact";
}

internal static partial class SchedulerLog
{
    [LoggerMessage(
        EventId = 2000,
        Level = LogLevel.Error,
        Message = "Scheduled {Operation} failed for {Tenant}/{Catalog}")]
    public static partial void JobFailed(ILogger logger, Exception exception, string operation, string tenant, string catalog);

    [LoggerMessage(
        EventId = 2001,
        Level = LogLevel.Information,
        Message = "Maintenance schedule: flush '{Flush}', backup '{Backup}', compact '{Compact}'")]
    public static partial void ScheduleConfigured(ILogger logger, string flush, string backup, string compact);

    [LoggerMessage(
        EventId = 2002,
        Level = LogLevel.Information,
        Message = "Maintenance scheduling is disabled")]
    public static partial void SchedulingDisabled(ILogger logger);
}

using Quartz;

namespace Lakehold.Api.Scheduling;

/// <summary>Cron schedules for automatic maintenance.</summary>
public sealed class MaintenanceScheduleOptions
{
    public const string SectionName = "Lakehold:Maintenance";

    /// <summary>Whether the scheduler runs at all. Off makes every operation manual.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    ///     Flush cadence. Bounds the window of permanently unrecoverable data, so it is the most
    ///     consequential of the three.
    /// </summary>
    public string FlushCron { get; set; } = "0 0/15 * * * ?";

    /// <summary>Catalog backup cadence. Bounds how much snapshot history a catalog loss costs.</summary>
    public string BackupCron { get; set; } = "0 0 * * * ?";

    /// <summary>Compaction cadence. I/O heavy, so it defaults to off-peak.</summary>
    public string CompactCron { get; set; } = "0 30 2 * * ?";

    /// <summary>
    ///     Identifies this node when claiming a maintenance lease.
    /// </summary>
    /// <remarks>
    ///     Defaults to the machine name, which is distinct per node in every deployment shape that
    ///     can share a catalog. Set it explicitly where that is not true — several containers on one
    ///     host would otherwise present the same identity and each be able to take over the others'
    ///     leases.
    /// </remarks>
    public string NodeId { get; set; } = Environment.MachineName;

    /// <summary>
    ///     How long a claimed lease stays valid if the node holding it never releases it.
    /// </summary>
    /// <remarks>
    ///     A crash-recovery bound, not a schedule: leases are released as soon as a job finishes.
    ///     It only needs to exceed the longest plausible single run, so that a slow compaction is
    ///     never mistaken for a dead node.
    /// </remarks>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(30);
}

/// <summary>Registers Quartz jobs for scheduled maintenance.</summary>
public static class SchedulingExtensions
{
    /// <summary>
    ///     Adds the maintenance scheduler.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Only non-destructive operations are scheduled: flush, backup, and compact. <c>expire</c>
    ///         and <c>cleanup</c> stay manual and dry-run-by-default on purpose — automating an
    ///         irreversible deletion would quietly undo the safety this product argues for.
    ///     </para>
    ///     <para>
    ///         Jobs are in-memory rather than using Quartz's <c>AdoJobStore</c>. Schedules are derived
    ///         from configuration, not user state, so there is nothing to persist and nothing to
    ///         migrate; a restart simply re-registers them.
    ///     </para>
    ///     <para>
    ///         That does mean every node in a cluster fires the same sweep at the same moment, so
    ///         the duplicate <em>work</em> is excluded by <see cref="MaintenanceLease"/> rather than
    ///         by centralising the <em>schedule</em>. The lease engages only for catalogs whose
    ///         metadata is in PostgreSQL, which is exactly the case where two nodes can reach one
    ///         catalog at all.
    ///     </para>
    /// </remarks>
    public static IHostApplicationBuilder AddMaintenanceScheduling(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.Configure<MaintenanceScheduleOptions>(
            builder.Configuration.GetSection(MaintenanceScheduleOptions.SectionName));

        var options = builder.Configuration
            .GetSection(MaintenanceScheduleOptions.SectionName)
            .Get<MaintenanceScheduleOptions>() ?? new MaintenanceScheduleOptions();

        builder.Services.AddSingleton<ScheduledRunLog>();

        if (!options.Enabled)
        {
            return builder;
        }

        builder.Services.AddQuartz(q =>
        {
            AddCronJob<FlushInlinedDataJob>(q, "flush", options.FlushCron);
            AddCronJob<CatalogBackupJob>(q, "backup", options.BackupCron);
            AddCronJob<CompactJob>(q, "compact", options.CompactCron);
        });

        // WaitForJobsToComplete matters here: shutting down mid-backup would leave a generation with
        // no manifest, which restore then refuses — recoverable, but it wastes a backup window.
        builder.Services.AddQuartzHostedService(q => q.WaitForJobsToComplete = true);

        return builder;
    }

    private static void AddCronJob<TJob>(IServiceCollectionQuartzConfigurator q, string name, string cron)
        where TJob : IJob
    {
        var key = new JobKey(name);
        q.AddJob<TJob>(key);
        q.AddTrigger(t => t
            .ForJob(key)
            .WithIdentity($"{name}-trigger")
            .WithCronSchedule(cron, x => x.WithMisfireHandlingInstructionDoNothing()));
    }

    /// <summary>Logs the effective schedule at start-up.</summary>
    public static void LogMaintenanceSchedule(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var options = app.Services.GetRequiredService<
            Microsoft.Extensions.Options.IOptions<MaintenanceScheduleOptions>>().Value;

        if (!options.Enabled)
        {
            SchedulerLog.SchedulingDisabled(app.Logger);
            return;
        }

        SchedulerLog.ScheduleConfigured(app.Logger, options.FlushCron, options.BackupCron, options.CompactCron);
    }
}

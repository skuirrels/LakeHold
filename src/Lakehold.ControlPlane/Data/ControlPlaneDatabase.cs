using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Lakehold.ControlPlane.Data;

/// <summary>Initialises the shared PostgreSQL control plane safely when several nodes start.</summary>
public static class ControlPlaneDatabase
{
    // Stable application-scoped advisory-lock key. PostgreSQL releases it if the connection dies.
    private const long MigrationLockKey = 5_499_457_981_173_219_923;
    private static readonly Action<ILogger, Exception?> MigrationsCurrent =
        LoggerMessage.Define(
            LogLevel.Information,
            new EventId(1000, nameof(MigrationsCurrent)),
            "PostgreSQL control-plane migrations are current");

    public static async Task MigrateAsync(IServiceProvider services, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(logger);

        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<ControlPlaneContext>();

        if (!context.Database.IsNpgsql())
        {
            throw new InvalidOperationException(
                $"The production control plane must use PostgreSQL; configured provider is "
                + $"'{context.Database.ProviderName ?? "unknown"}'.");
        }

        await context.Database.OpenConnectionAsync().ConfigureAwait(false);
        try
        {
            await context.Database
                .ExecuteSqlRawAsync($"SELECT pg_advisory_lock({MigrationLockKey})")
                .ConfigureAwait(false);
            try
            {
                await context.Database.MigrateAsync().ConfigureAwait(false);
                MigrationsCurrent(logger, null);
            }
            finally
            {
                await context.Database
                    .ExecuteSqlRawAsync($"SELECT pg_advisory_unlock({MigrationLockKey})")
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            await context.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }
}

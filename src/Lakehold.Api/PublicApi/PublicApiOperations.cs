using System.Text.Json;
using Lakehold.Api.Auth;
using Lakehold.ControlPlane.Data;
using Lakehold.ControlPlane.Model;
using Lakehold.ControlPlane.Security;
using Lakehold.Engine.Catalog;
using Microsoft.EntityFrameworkCore;

namespace Lakehold.Api.PublicApi;

public static class PublicApiOperationKinds
{
    public const string Maintenance = "maintenance";
    public const string RestoreBackup = "restore-backup";
    public const string Eject = "eject";
}

public sealed record MaintenanceOperationRequest(string Operation, bool Apply);

public sealed record RestoreBackupOperationRequest(string? Generation, string TargetMetadataPath);

public sealed record EjectOperationRequest(bool IncludeHistory);

public sealed record PublicApiOperationDto(
    string Id,
    string Kind,
    string Status,
    object? Result,
    string? Error,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? StartedUtc,
    DateTimeOffset? CompletedUtc);

/// <summary>Creates, claims, and completes durable API operations in shared PostgreSQL state.</summary>
public sealed class PublicApiOperationStore(ControlPlaneContext context, TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static readonly TimeSpan TerminalRecordRetention = TimeSpan.FromDays(30);

    public async Task<ApiOperation> EnqueueAsync<TRequest>(
        string tenantSlug,
        string catalogName,
        string kind,
        TRequest request,
        int? tokenId,
        CancellationToken cancellationToken)
    {
        var operation = new ApiOperation
        {
            Id = Guid.NewGuid().ToString("N"),
            TenantSlug = tenantSlug,
            CatalogName = catalogName,
            Kind = kind,
            RequestJson = JsonSerializer.Serialize(request, Json),
            Status = ApiOperationStatus.Queued,
            RequestedByTokenId = tokenId,
            CreatedUtc = clock.GetUtcNow(),
        };
        context.ApiOperations.Add(operation);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return operation;
    }

    public Task<ApiOperation?> GetAsync(string id, CancellationToken cancellationToken)
        => context.ApiOperations.AsNoTracking()
            .SingleOrDefaultAsync(operation => operation.Id == id, cancellationToken);

    public async Task<ApiOperation?> ClaimNextAsync(
        string nodeId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var now = clock.GetUtcNow();
            var candidate = await context.ApiOperations.AsNoTracking()
                .Where(operation => operation.Status == ApiOperationStatus.Queued)
                .OrderBy(operation => operation.CreatedUtc)
                .ThenBy(operation => operation.Id)
                .Select(operation => new { operation.Id, operation.ConcurrencyVersion })
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            if (candidate is null)
            {
                return null;
            }

            var claimed = await context.ApiOperations
                .Where(operation => operation.Id == candidate.Id
                    && operation.Status == ApiOperationStatus.Queued
                    && operation.ConcurrencyVersion == candidate.ConcurrencyVersion)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(operation => operation.Status, ApiOperationStatus.Running)
                    .SetProperty(operation => operation.StartedUtc, now)
                    .SetProperty(operation => operation.LeaseOwner, nodeId)
                    .SetProperty(operation => operation.LeaseExpiresUtc, now.Add(leaseDuration))
                    .SetProperty(operation => operation.ConcurrencyVersion, operation => operation.ConcurrencyVersion + 1),
                    cancellationToken)
                .ConfigureAwait(false);
            if (claimed != 1)
            {
                context.ChangeTracker.Clear();
                continue;
            }

            context.ChangeTracker.Clear();
            return await context.ApiOperations.SingleAsync(
                    operation => operation.Id == candidate.Id,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public Task<int> MarkExpiredLeasesIndeterminateAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        return context.ApiOperations
            .Where(operation => operation.Status == ApiOperationStatus.Running
                && operation.LeaseExpiresUtc <= now)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(operation => operation.Status, ApiOperationStatus.Indeterminate)
                .SetProperty(operation => operation.Error, "The worker lease expired; LakeHold will not execute the operation again automatically.")
                .SetProperty(operation => operation.CompletedUtc, now)
                .SetProperty(operation => operation.LeaseOwner, (string?)null)
                .SetProperty(operation => operation.LeaseExpiresUtc, (DateTimeOffset?)null)
                .SetProperty(operation => operation.ConcurrencyVersion, operation => operation.ConcurrencyVersion + 1),
                cancellationToken);
    }

    public async Task CompleteAsync(
        ApiOperation operation,
        object result,
        CancellationToken cancellationToken)
    {
        var completedUtc = clock.GetUtcNow();
        var resultJson = JsonSerializer.Serialize(result, Json);
        var updated = await context.ApiOperations
            .Where(candidate => candidate.Id == operation.Id
                && candidate.Status == ApiOperationStatus.Running
                && candidate.LeaseOwner == operation.LeaseOwner
                && candidate.LeaseExpiresUtc > completedUtc)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(candidate => candidate.ResultJson, resultJson)
                .SetProperty(candidate => candidate.Error, (string?)null)
                .SetProperty(candidate => candidate.Status, ApiOperationStatus.Succeeded)
                .SetProperty(candidate => candidate.CompletedUtc, completedUtc)
                .SetProperty(candidate => candidate.LeaseOwner, (string?)null)
                .SetProperty(candidate => candidate.LeaseExpiresUtc, (DateTimeOffset?)null)
                .SetProperty(candidate => candidate.ConcurrencyVersion, candidate => candidate.ConcurrencyVersion + 1),
                cancellationToken)
            .ConfigureAwait(false);
        EnsureLeaseOwned(updated);
    }

    public async Task FailAsync(
        ApiOperation operation,
        string safeError,
        CancellationToken cancellationToken)
    {
        var completedUtc = clock.GetUtcNow();
        var error = PublicApiProblems.BoundedDetail(safeError);
        var updated = await context.ApiOperations
            .Where(candidate => candidate.Id == operation.Id
                && candidate.Status == ApiOperationStatus.Running
                && candidate.LeaseOwner == operation.LeaseOwner
                && candidate.LeaseExpiresUtc > completedUtc)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(candidate => candidate.Error, error)
                .SetProperty(candidate => candidate.Status, ApiOperationStatus.Failed)
                .SetProperty(candidate => candidate.CompletedUtc, completedUtc)
                .SetProperty(candidate => candidate.LeaseOwner, (string?)null)
                .SetProperty(candidate => candidate.LeaseExpiresUtc, (DateTimeOffset?)null)
                .SetProperty(candidate => candidate.ConcurrencyVersion, candidate => candidate.ConcurrencyVersion + 1),
                cancellationToken)
            .ConfigureAwait(false);
        EnsureLeaseOwned(updated);
    }

    public async Task<bool> RenewLeaseAsync(
        string id,
        string leaseOwner,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var expiresUtc = now.Add(leaseDuration);
        return await context.ApiOperations
            .Where(operation => operation.Id == id
                && operation.Status == ApiOperationStatus.Running
                && operation.LeaseOwner == leaseOwner
                && operation.LeaseExpiresUtc > now)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(operation => operation.LeaseExpiresUtc, expiresUtc)
                .SetProperty(operation => operation.ConcurrencyVersion, operation => operation.ConcurrencyVersion + 1),
                cancellationToken)
            .ConfigureAwait(false) == 1;
    }

    public Task<int> DeleteExpiredTerminalAsync(CancellationToken cancellationToken)
    {
        var cutoff = clock.GetUtcNow().Subtract(TerminalRecordRetention);
        return context.ApiOperations
            .Where(operation => operation.CompletedUtc < cutoff
                && (operation.Status == ApiOperationStatus.Succeeded
                    || operation.Status == ApiOperationStatus.Failed
                    || operation.Status == ApiOperationStatus.Indeterminate))
            .ExecuteDeleteAsync(cancellationToken);
    }

    public static PublicApiOperationDto ToDto(ApiOperation operation)
        => new(
            operation.Id,
            operation.Kind,
            operation.Status.ToString().ToLowerInvariant(),
            operation.ResultJson is null
                ? null
                : JsonSerializer.Deserialize<JsonElement>(operation.ResultJson, Json),
            operation.Error,
            operation.CreatedUtc,
            operation.StartedUtc,
            operation.CompletedUtc);

    private static void EnsureLeaseOwned(int updated)
    {
        if (updated != 1)
        {
            throw new InvalidOperationException(
                "The public API operation lease was lost before completion could be recorded.");
        }
    }
}

/// <summary>Executes durable API work outside request lifetimes.</summary>
public sealed partial class PublicApiOperationWorker(
    IServiceScopeFactory scopes,
    IHostApplicationLifetime lifetime,
    TimeProvider clock,
    ILogger<PublicApiOperationWorker> logger) : BackgroundService
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan LeaseSweepInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan IdlePollInterval = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _nodeId = string.Concat(Environment.MachineName, "-", Guid.NewGuid().ToString("N")[..12]);
    private DateTimeOffset _nextLeaseSweepUtc = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        while (!stoppingToken.IsCancellationRequested && !lifetime.ApplicationStopping.IsCancellationRequested)
        {
            var delay = IdlePollInterval;
            try
            {
                if (await ExecuteOneAsync(stoppingToken).ConfigureAwait(false))
                {
                    delay = TimeSpan.Zero;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                LogSweepFailure(logger, exception);
            }

            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    internal async Task<bool> ExecuteOneAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<PublicApiOperationStore>();
        var now = clock.GetUtcNow();
        if (now >= _nextLeaseSweepUtc)
        {
            await store.MarkExpiredLeasesIndeterminateAsync(cancellationToken).ConfigureAwait(false);
            _nextLeaseSweepUtc = now.Add(LeaseSweepInterval);
        }

        var operation = await store.ClaimNextAsync(_nodeId, LeaseDuration, cancellationToken)
            .ConfigureAwait(false);
        if (operation is null)
        {
            return false;
        }

        using var execution = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var heartbeat = MaintainLeaseAsync(operation, execution);
        try
        {
            var lakehouse = scope.ServiceProvider.GetRequiredService<LakehouseService>();
            object result = operation.Kind switch
            {
                PublicApiOperationKinds.Maintenance => await ExecuteMaintenanceAsync(
                    operation, lakehouse, execution.Token).ConfigureAwait(false),
                PublicApiOperationKinds.RestoreBackup => await ExecuteRestoreAsync(
                    operation, lakehouse, execution.Token).ConfigureAwait(false),
                PublicApiOperationKinds.Eject => await ExecuteEjectAsync(
                    operation, lakehouse, execution.Token).ConfigureAwait(false),
                _ => throw new InvalidOperationException($"Unknown public API operation kind '{operation.Kind}'."),
            };
            execution.Token.ThrowIfCancellationRequested();
            await store.CompleteAsync(operation, result, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            execution.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // The heartbeat already logged the lost lease. Never complete or retry work whose
            // ownership is no longer provable.
        }
        catch (Exception) when (
            execution.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // Lease renewal failed or ownership was lost. Even if the implementation surfaced a
            // different exception while cancellation propagated, this worker no longer has
            // authority to record a terminal result.
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogOperationFailure(logger, exception, operation.Id);
            await store.FailAsync(
                    operation,
                    $"Operation '{operation.Id}' failed. Review the server log for its error type.",
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            await execution.CancelAsync().ConfigureAwait(false);
            try
            {
                await heartbeat.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (execution.IsCancellationRequested)
            {
                // Normal completion cancels the delayed heartbeat.
            }
        }

        return true;
    }

    private async Task MaintainLeaseAsync(ApiOperation operation, CancellationTokenSource execution)
    {
        try
        {
            while (!execution.IsCancellationRequested)
            {
                await Task.Delay(LeaseDuration / 3, execution.Token).ConfigureAwait(false);
                await using var scope = scopes.CreateAsyncScope();
                var store = scope.ServiceProvider.GetRequiredService<PublicApiOperationStore>();
                if (operation.LeaseOwner is null
                    || !await store.RenewLeaseAsync(
                            operation.Id,
                            operation.LeaseOwner,
                            LeaseDuration,
                            execution.Token)
                        .ConfigureAwait(false))
                {
                    LogLeaseLost(logger, operation.Id);
                    await execution.CancelAsync().ConfigureAwait(false);
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (execution.IsCancellationRequested)
        {
            // Normal completion or lease-loss cancellation.
        }
        catch (Exception exception)
        {
            LogLeaseRenewalFailure(logger, exception, operation.Id);
            await execution.CancelAsync().ConfigureAwait(false);
        }
    }

    private static async Task<object> ExecuteMaintenanceAsync(
        ApiOperation operation,
        LakehouseService lakehouse,
        CancellationToken cancellationToken)
    {
        var request = Deserialize<MaintenanceOperationRequest>(operation);
        var result = await lakehouse.RunMaintenanceAsync(
                operation.TenantSlug,
                operation.CatalogName,
                request.Operation,
                request.Apply,
                cancellationToken)
            .ConfigureAwait(false);
        return new
        {
            result.Operation,
            result.Detail,
            elapsedMilliseconds = result.Elapsed.TotalMilliseconds,
            result.DryRun,
        };
    }

    private static async Task<object> ExecuteRestoreAsync(
        ApiOperation operation,
        LakehouseService lakehouse,
        CancellationToken cancellationToken)
    {
        var request = Deserialize<RestoreBackupOperationRequest>(operation);
        var result = await lakehouse.RestoreBackupAsync(
                operation.TenantSlug,
                operation.CatalogName,
                request.Generation,
                request.TargetMetadataPath,
                cancellationToken)
            .ConfigureAwait(false);
        return new
        {
            result.Generation,
            result.TablesRestored,
            result.RowsRestored,
        };
    }

    private static async Task<object> ExecuteEjectAsync(
        ApiOperation operation,
        LakehouseService lakehouse,
        CancellationToken cancellationToken)
    {
        var request = Deserialize<EjectOperationRequest>(operation);
        var result = await lakehouse.EjectAsync(
                operation.TenantSlug,
                operation.CatalogName,
                request.IncludeHistory,
                cancellationToken)
            .ConfigureAwait(false);
        return new
        {
            tableCount = result.TableCount,
            result.TotalRows,
            result.Verified,
            result.DigestDeferred,
            isSigned = result.IsSigned,
            includesHistory = result.IncludesHistory,
        };
    }

    private static TRequest Deserialize<TRequest>(ApiOperation operation)
        => JsonSerializer.Deserialize<TRequest>(operation.RequestJson, Json)
            ?? throw new InvalidOperationException("The durable API operation request is invalid.");

    [LoggerMessage(
        EventId = 2350,
        Level = LogLevel.Error,
        Message = "Public API operation sweep failed.")]
    private static partial void LogSweepFailure(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 2351,
        Level = LogLevel.Error,
        Message = "Public API operation {OperationId} failed.")]
    private static partial void LogOperationFailure(
        ILogger logger,
        Exception exception,
        string operationId);

    [LoggerMessage(
        EventId = 2352,
        Level = LogLevel.Error,
        Message = "Public API operation {OperationId} lost its durable lease and was cancelled.")]
    private static partial void LogLeaseLost(ILogger logger, string operationId);

    [LoggerMessage(
        EventId = 2353,
        Level = LogLevel.Error,
        Message = "Public API operation {OperationId} lease renewal failed; execution was cancelled.")]
    private static partial void LogLeaseRenewalFailure(
        ILogger logger,
        Exception exception,
        string operationId);
}

public static class PublicApiOperationEndpoints
{
    public static RouteGroupBuilder MapPublicApiOperations(this RouteGroupBuilder api)
    {
        api.MapGet("/operations/{id}", GetAsync)
            .AddEndpointFilter<LakeholdAuthorizationFilter>()
            .RequireCapability(Capability.Listing)
            .WithTags("Operations")
            .WithName("GetOperation")
            .Produces<PublicApiOperationDto>()
            .WithSummary("Returns one durable public API operation visible to the caller.");
        return api;
    }

    private static async Task<IResult> GetAsync(
        string id,
        HttpContext http,
        PublicApiOperationStore operations,
        CancellationToken cancellationToken)
    {
        var operation = await operations.GetAsync(id, cancellationToken).ConfigureAwait(false);
        var principal = http.GetLakeholdPrincipal();
        if (operation is null || !IsVisibleTo(principal, operation))
        {
            return Results.NotFound("The operation was not found.");
        }

        if (operation.Status is ApiOperationStatus.Queued or ApiOperationStatus.Running)
        {
            http.Response.Headers.RetryAfter = "1";
        }

        return Results.Ok(PublicApiOperationStore.ToDto(operation));
    }

    internal static bool IsVisibleTo(ILakeholdPrincipal principal, ApiOperation operation)
    {
        if (principal.Scope == TokenScope.Instance)
        {
            return true;
        }

        return string.Equals(principal.TenantSlug, operation.TenantSlug, StringComparison.Ordinal)
            && (principal.CatalogName is null
                || string.Equals(principal.CatalogName, operation.CatalogName, StringComparison.Ordinal));
    }
}

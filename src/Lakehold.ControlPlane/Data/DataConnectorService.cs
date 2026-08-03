using Lakehold.ControlPlane.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Lakehold.ControlPlane.Data;

/// <summary>Raised when a connector name or optimistic version conflicts with durable state.</summary>
public sealed class DataConnectorConflictException(string message) : Exception(message);

/// <summary>A durable execution claim acquired by exactly one worker node.</summary>
public sealed record DataConnectorClaim(
    DataConnector Connector,
    int RunId,
    string NodeId,
    string LeaseToken);

/// <summary>Raised when a worker attempts to act after its claim has expired or been replaced.</summary>
public sealed class DataConnectorLeaseLostException(string message) : Exception(message);

/// <summary>
///     Holds the PostgreSQL connector row lock while DuckLake publication is in progress. A new
///     claimant cannot pass this fence until publication and durable completion have finished.
/// </summary>
public sealed class DataConnectorPublicationFence : IAsyncDisposable
{
    private readonly ControlPlaneContext _context;
    private readonly IDbContextTransaction _transaction;
    private readonly DataConnector _connector;
    private readonly DataConnectorRun _run;
    private readonly string _leaseToken;
    private bool _completed;

    internal DataConnectorPublicationFence(
        ControlPlaneContext context,
        IDbContextTransaction transaction,
        DataConnector connector,
        DataConnectorRun run,
        string leaseToken)
    {
        _context = context;
        _transaction = transaction;
        _connector = connector;
        _run = run;
        _leaseToken = leaseToken;
    }

    public async Task CompleteAsync(
        DateTimeOffset now,
        long rowsRead,
        long rowsPublished,
        string? sourceVersion,
        string? proposedCheckpoint,
        string? replayKey,
        bool targetPublished,
        CancellationToken cancellationToken)
    {
        _run.Succeed(now, rowsRead, rowsPublished, sourceVersion, proposedCheckpoint, replayKey);
        _connector.MarkSucceeded(_leaseToken, now, targetPublished, proposedCheckpoint);
        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await _transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        _completed = true;
    }

    public Task CompleteAsync(
        DateTimeOffset now,
        long rowsRead,
        long rowsPublished,
        string? sourceVersion,
        CancellationToken cancellationToken) => CompleteAsync(
        now,
        rowsRead,
        rowsPublished,
        sourceVersion,
        proposedCheckpoint: null,
        replayKey: null,
        targetPublished: true,
        cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (!_completed)
        {
            await _transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }

        await _transaction.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>
///     Application service for connector definitions, lineage runs, and multi-node-safe execution
///     leases. Protocol I/O remains in infrastructure adapters in <c>Lakehold.Api</c>.
/// </summary>
public sealed class DataConnectorService(ControlPlaneContext context)
{
    private readonly ControlPlaneContext _context = context;

    public async Task<IReadOnlyList<DataConnector>> ListAsync(
        string tenantSlug,
        string catalogName,
        CancellationToken cancellationToken) => await Query(tenantSlug, catalogName)
        .AsNoTracking()
        .OrderBy(connector => connector.Name)
        .ToListAsync(cancellationToken)
        .ConfigureAwait(false);

    public async Task<DataConnector> GetAsync(
        string tenantSlug,
        string catalogName,
        int id,
        CancellationToken cancellationToken) => await Query(tenantSlug, catalogName)
        .AsNoTracking()
        .SingleOrDefaultAsync(connector => connector.Id == id, cancellationToken)
        .ConfigureAwait(false)
        ?? throw new CatalogNotFoundException(
            $"Connector '{id}' was not found in catalog '{catalogName}' for tenant '{tenantSlug}'.");

    public async Task<DataConnector> CreateAsync(
        string tenantSlug,
        string catalogName,
        DataConnectorDefinition definition,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var catalog = await _context.Catalogs
            .Include(item => item.Tenant)
            .SingleOrDefaultAsync(
                item => item.Tenant.Slug == tenantSlug && item.Name == catalogName,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new CatalogNotFoundException(
                $"Catalog '{catalogName}' was not found for tenant '{tenantSlug}'.");

        var connector = DataConnector.Create(catalog.TenantId, catalog.Id, definition, now);
        if (await _context.DataConnectors.AnyAsync(
                item => item.CatalogId == catalog.Id && item.Name == connector.Name,
                cancellationToken)
            .ConfigureAwait(false))
        {
            throw new DataConnectorConflictException(
                $"A connector named '{connector.Name}' already exists in this catalog.");
        }

        if (await _context.DataConnectors.AnyAsync(
                item => item.CatalogId == catalog.Id
                        && item.TargetSchema == connector.TargetSchema
                        && item.TargetTable == connector.TargetTable,
                cancellationToken)
            .ConfigureAwait(false))
        {
            throw new DataConnectorConflictException(
                $"Target '{connector.TargetSchema}.{connector.TargetTable}' is already owned by another connector.");
        }

        _context.DataConnectors.Add(connector);
        try
        {
            await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex, "IX_DataConnectors_CatalogId_Name"))
        {
            throw new DataConnectorConflictException(
                $"A connector named '{definition.Name.Trim()}' already exists in this catalog.");
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(
                   ex,
                   "IX_DataConnectors_CatalogId_TargetSchema_TargetTable"))
        {
            throw new DataConnectorConflictException(
                $"Target '{definition.TargetSchema.Trim()}.{definition.TargetTable.Trim()}' is already owned by another connector.");
        }

        return connector;
    }

    public async Task<DataConnector> UpdateAsync(
        string tenantSlug,
        string catalogName,
        int id,
        int expectedVersion,
        DataConnectorDefinition definition,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var connector = await Query(tenantSlug, catalogName)
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new CatalogNotFoundException(
                $"Connector '{id}' was not found in catalog '{catalogName}' for tenant '{tenantSlug}'.");
        if (connector.ConcurrencyVersion != expectedVersion)
        {
            throw new DataConnectorConflictException(
                $"Connector '{connector.Name}' changed since version {expectedVersion}; reload it before updating.");
        }

        if (connector.LeaseExpiresUtc > now)
        {
            throw new DataConnectorConflictException(
                $"Connector '{connector.Name}' is currently refreshing and cannot be updated.");
        }

        if (connector.ArchivedUtc is not null)
        {
            throw new DataConnectorConflictException(
                $"Connector '{connector.Name}' is archived and cannot be updated.");
        }

        if (connector.TargetProvisioned
            && (!string.Equals(connector.TargetSchema, definition.TargetSchema.Trim(), StringComparison.Ordinal)
                || !string.Equals(connector.TargetTable, definition.TargetTable.Trim(), StringComparison.Ordinal)))
        {
            throw new DataConnectorConflictException(
                "A connector target cannot change after its first successful publication; create a new connector instead.");
        }

        connector.Reconfigure(definition, now);
        try
        {
            await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new DataConnectorConflictException(
                $"Connector '{connector.Name}' changed while it was being updated; reload it and retry.");
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex, "IX_DataConnectors_CatalogId_Name"))
        {
            throw new DataConnectorConflictException(
                $"A connector named '{definition.Name.Trim()}' already exists in this catalog.");
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(
                   ex,
                   "IX_DataConnectors_CatalogId_TargetSchema_TargetTable"))
        {
            throw new DataConnectorConflictException(
                $"Target '{definition.TargetSchema.Trim()}.{definition.TargetTable.Trim()}' is already owned by another connector.");
        }

        return connector;
    }

    public async Task DeleteAsync(
        string tenantSlug,
        string catalogName,
        int id,
        CancellationToken cancellationToken)
    {
        var connector = await Query(tenantSlug, catalogName)
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new CatalogNotFoundException(
                $"Connector '{id}' was not found in catalog '{catalogName}' for tenant '{tenantSlug}'.");
        if (connector.LeaseExpiresUtc > DateTimeOffset.UtcNow)
        {
            throw new DataConnectorConflictException(
                $"Connector '{connector.Name}' is currently refreshing and cannot be deleted.");
        }

        connector.Archive(DateTimeOffset.UtcNow);
        try
        {
            await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new DataConnectorConflictException(
                $"Connector '{connector.Name}' changed while it was being archived; reload it and retry.");
        }
    }

    public async Task<DataConnector> PauseAsync(
        string tenantSlug,
        string catalogName,
        int id,
        int expectedVersion,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var connector = await MutableAsync(tenantSlug, catalogName, id, expectedVersion, now, cancellationToken)
            .ConfigureAwait(false);
        connector.Pause(now);
        await SaveOperationalChangeAsync(connector, cancellationToken).ConfigureAwait(false);
        return connector;
    }

    public async Task<DataConnector> ResumeAsync(
        string tenantSlug,
        string catalogName,
        int id,
        int expectedVersion,
        bool resetFailures,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var connector = await MutableAsync(tenantSlug, catalogName, id, expectedVersion, now, cancellationToken)
            .ConfigureAwait(false);
        connector.Resume(now, resetFailures);
        await SaveOperationalChangeAsync(connector, cancellationToken).ConfigureAwait(false);
        return connector;
    }

    public async Task<IReadOnlyList<DataConnectorRun>> ListRunsAsync(
        string tenantSlug,
        string catalogName,
        int id,
        int limit,
        CancellationToken cancellationToken)
    {
        _ = await GetAsync(tenantSlug, catalogName, id, cancellationToken).ConfigureAwait(false);
        return await _context.DataConnectorRuns
            .AsNoTracking()
            .Where(run => run.DataConnectorId == id)
            .OrderByDescending(run => run.StartedUtc)
            .ThenByDescending(run => run.Id)
            .Take(Math.Max(limit, 1))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DataConnectorRun>> ListRunsAsync(
        string tenantSlug,
        string catalogName,
        int id,
        DataConnectorRunStatus status,
        int limit,
        CancellationToken cancellationToken)
    {
        _ = await GetAsync(tenantSlug, catalogName, id, cancellationToken).ConfigureAwait(false);
        return await _context.DataConnectorRuns
            .AsNoTracking()
            .Where(run => run.DataConnectorId == id && run.Status == status)
            .OrderByDescending(run => run.StartedUtc)
            .ThenByDescending(run => run.Id)
            .Take(Math.Max(limit, 1))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<DataConnectorClaim?> TryClaimAsync(
        int id,
        DataConnectorTrigger trigger,
        string nodeId,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(nodeId) || nodeId.Length > 128)
        {
            throw new ArgumentException("Worker node ids must contain 1 to 128 characters.", nameof(nodeId));
        }

        if (leaseDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration), "Lease duration must be positive.");
        }

        var leaseToken = Guid.NewGuid().ToString("N");
        await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var candidates = _context.DataConnectors.Where(connector =>
                connector.Id == id
                && connector.ArchivedUtc == null
                && connector.PausedUtc == null
                && (connector.LeaseExpiresUtc == null || connector.LeaseExpiresUtc <= now));
            if (trigger == DataConnectorTrigger.Scheduled)
            {
                candidates = candidates.Where(connector =>
                    connector.Enabled
                    && connector.RefreshIntervalSeconds != null
                    && connector.NextRunUtc != null
                    && connector.NextRunUtc <= now);
            }

            var claimed = await candidates.ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(connector => connector.LeaseOwner, nodeId)
                        .SetProperty(connector => connector.LeaseExpiresUtc, now.Add(leaseDuration))
                        .SetProperty(connector => connector.LeaseToken, leaseToken)
                        .SetProperty(connector => connector.ConcurrencyVersion, connector => connector.ConcurrencyVersion + 1),
                    cancellationToken)
                .ConfigureAwait(false);
            if (claimed != 1)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }

            _context.ChangeTracker.Clear();
            var connector = await _context.DataConnectors
                .AsNoTracking()
                .Include(item => item.Tenant)
                .Include(item => item.Catalog)
                .SingleAsync(item => item.Id == id, cancellationToken)
                .ConfigureAwait(false);

            await CloseAbandonedRunsAsync(id, now, cancellationToken).ConfigureAwait(false);
            var run = DataConnectorRun.Start(id, trigger, nodeId, leaseToken, now, connector.Checkpoint);
            _context.DataConnectorRuns.Add(run);
            await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new DataConnectorClaim(connector, run.Id, nodeId, leaseToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            _context.ChangeTracker.Clear();
            throw;
        }
    }

    public async Task<int?> FindNextDueIdAsync(DateTimeOffset now, CancellationToken cancellationToken) =>
        await _context.DataConnectors
            .AsNoTracking()
            .Where(connector =>
                connector.Enabled
                && connector.ArchivedUtc == null
                && connector.PausedUtc == null
                && connector.RefreshIntervalSeconds != null
                && connector.NextRunUtc != null
                && connector.NextRunUtc <= now
                && (connector.LeaseExpiresUtc == null || connector.LeaseExpiresUtc <= now))
            .OrderBy(connector => connector.NextRunUtc)
            .Select(connector => (int?)connector.Id)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task CompleteFailureAsync(
        DataConnectorClaim claim,
        DateTimeOffset now,
        long rowsRead,
        string? sourceVersion,
        bool? qualityPassed,
        string error,
        CancellationToken cancellationToken,
        string? proposedCheckpoint = null,
        string? replayKey = null)
    {
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentException.ThrowIfNullOrWhiteSpace(error);
        await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        var locked = await CurrentClaim(claim, now)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(connector => connector.LeaseExpiresUtc, connector => connector.LeaseExpiresUtc),
                cancellationToken)
            .ConfigureAwait(false);
        if (locked != 1)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new DataConnectorLeaseLostException(
                $"Connector '{claim.Connector.Name}' is no longer owned by this execution.");
        }

        _context.ChangeTracker.Clear();
        var connector = await _context.DataConnectors.SingleAsync(
                item => item.Id == claim.Connector.Id,
                cancellationToken)
            .ConfigureAwait(false);
        var run = await _context.DataConnectorRuns.SingleAsync(
                item => item.Id == claim.RunId,
                cancellationToken)
            .ConfigureAwait(false);

        if (run.Status != DataConnectorRunStatus.Running
            || !string.Equals(run.LeaseToken, claim.LeaseToken, StringComparison.Ordinal))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new DataConnectorLeaseLostException(
                $"Connector run '{claim.RunId}' is no longer the current execution.");
        }

        var deadLettered = connector.MarkFailed(claim.LeaseToken, now, error);
        run.Fail(
            now,
            rowsRead,
            sourceVersion,
            qualityPassed,
            error,
            deadLettered,
            proposedCheckpoint,
            replayKey);
        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<DataConnectorPublicationFence?> TryBeginPublicationAsync(
        DataConnectorClaim claim,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(claim);
        var transaction = await _context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var locked = await CurrentClaim(claim, now)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(
                        connector => connector.LeaseExpiresUtc,
                        connector => connector.LeaseExpiresUtc),
                    cancellationToken)
                .ConfigureAwait(false);
            if (locked != 1)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                await transaction.DisposeAsync().ConfigureAwait(false);
                return null;
            }

            _context.ChangeTracker.Clear();
            var connector = await _context.DataConnectors.SingleAsync(
                    item => item.Id == claim.Connector.Id,
                    cancellationToken)
                .ConfigureAwait(false);
            var run = await _context.DataConnectorRuns.SingleAsync(
                    item => item.Id == claim.RunId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (run.Status != DataConnectorRunStatus.Running
                || !string.Equals(run.LeaseToken, claim.LeaseToken, StringComparison.Ordinal))
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                await transaction.DisposeAsync().ConfigureAwait(false);
                return null;
            }

            return new DataConnectorPublicationFence(_context, transaction, connector, run, claim.LeaseToken);
        }
        catch
        {
            await transaction.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private IQueryable<DataConnector> Query(string tenantSlug, string catalogName) =>
        _context.DataConnectors.Where(connector =>
            connector.Tenant.Slug == tenantSlug && connector.Catalog.Name == catalogName);

    private IQueryable<DataConnector> CurrentClaim(DataConnectorClaim claim, DateTimeOffset now) =>
        _context.DataConnectors.Where(connector =>
            connector.Id == claim.Connector.Id
            && connector.ArchivedUtc == null
            && connector.LeaseOwner == claim.NodeId
            && connector.LeaseToken == claim.LeaseToken
            && connector.LeaseExpiresUtc > now);

    private async Task<DataConnector> MutableAsync(
        string tenantSlug,
        string catalogName,
        int id,
        int expectedVersion,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var connector = await Query(tenantSlug, catalogName)
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new CatalogNotFoundException(
                $"Connector '{id}' was not found in catalog '{catalogName}' for tenant '{tenantSlug}'.");
        if (connector.ConcurrencyVersion != expectedVersion)
        {
            throw new DataConnectorConflictException(
                $"Connector '{connector.Name}' changed since version {expectedVersion}; reload it before updating.");
        }

        if (connector.LeaseExpiresUtc > now)
        {
            throw new DataConnectorConflictException(
                $"Connector '{connector.Name}' is currently refreshing and cannot be changed.");
        }

        return connector;
    }

    private async Task SaveOperationalChangeAsync(
        DataConnector connector,
        CancellationToken cancellationToken)
    {
        try
        {
            await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new DataConnectorConflictException(
                $"Connector '{connector.Name}' changed while its operational state was being updated.");
        }
    }

    private static bool IsUniqueViolation(DbUpdateException exception, string constraintName) =>
        exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: var actualConstraint,
        }
        && string.Equals(actualConstraint, constraintName, StringComparison.Ordinal);

    private async Task CloseAbandonedRunsAsync(
        int connectorId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var abandoned = await _context.DataConnectorRuns
            .Where(run => run.DataConnectorId == connectorId && run.Status == DataConnectorRunStatus.Running)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (var run in abandoned)
        {
            run.Fail(
                now,
                run.RowsRead,
                run.SourceVersion,
                qualityPassed: null,
                "The worker lease expired before the refresh completed.");
        }
    }
}

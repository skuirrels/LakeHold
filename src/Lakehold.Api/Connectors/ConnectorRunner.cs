using System.Diagnostics;
using Lakehold.ControlPlane.Data;
using Lakehold.ControlPlane.Model;
using Lakehold.Engine.Catalog;
using Lakehold.Engine.Telemetry;
using Microsoft.Extensions.Options;

namespace Lakehold.Api.Connectors;

internal enum ConnectorExecutionFailureKind
{
    None,
    ClaimConflict,
    Quality,
    TargetConflict,
    Capacity,
    SourceOrImport,
    PublicationState,
}

internal sealed record ConnectorExecutionResult(
    int RunId,
    string Status,
    long RowsRead,
    long RowsPublished,
    string? SourceVersion,
    string? Error,
    ConnectorExecutionFailureKind FailureKind = ConnectorExecutionFailureKind.None);

/// <summary>Coordinates one durable claim, protocol read, quality gate, and atomic publication.</summary>
internal sealed class ConnectorRunner(
    DataConnectorService connectors,
    DataConnectorSourceResolver sources,
    LakehouseService lakehouse,
    ConnectorScratchSpace scratch,
    IOptions<ConnectorOptions> options,
    ILogger<ConnectorRunner> logger)
{
    private static readonly string NodeId = BuildNodeId();

    public async Task<ConnectorExecutionResult?> RunAsync(
        int connectorId,
        DataConnectorTrigger trigger,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var claim = await connectors.TryClaimAsync(
                connectorId,
                trigger,
                NodeId,
                now,
                options.Value.LeaseDuration,
                cancellationToken)
            .ConfigureAwait(false);
        if (claim is null)
        {
            LakeholdTelemetry.ConnectorLeaseConflicts.Add(1);
            return null;
        }

        var connector = claim.Connector;
        using var activity = LakeholdTelemetry.Source.StartActivity("lakehold.connector.refresh");
        activity?.SetTag(LakeholdTelemetry.TenantKey, connector.Tenant.Slug);
        activity?.SetTag(LakeholdTelemetry.CatalogKey, connector.Catalog.Name);
        activity?.SetTag("lakehold.connector.kind", connector.Kind.ToString().ToLowerInvariant());
        activity?.SetTag("lakehold.connector.id", connector.Id);
        var startedAt = Stopwatch.GetTimestamp();
        var rowsRead = 0L;
        string? sourceVersion = null;
        string? proposedCheckpoint = null;
        string? replayKey = null;
        JsonSnapshotImportResult? published = null;
        ConnectorSnapshotFile? snapshot = null;
        bool? qualityPassed = null;
        LakeholdTelemetry.ConnectorWorkersActive.Add(1);

        try
        {
            await using var ownedSnapshot = await ConnectorSnapshotFile.CreateAsync(
                scratch,
                options,
                cancellationToken,
                failureType => ConnectorLog.ScratchCleanupFailed(
                    logger,
                    connector.Id,
                    connector.Name,
                    failureType)).ConfigureAwait(false);
            snapshot = ownedSnapshot;
            snapshot.ConfigureMappings(connector.FieldMappings());
            var source = sources.Resolve(connector);
            var sourceResult = await source.ReadAsync(
                    new ConnectorReadContext(
                        connector,
                        connector.Checkpoint,
                        connector.Tenant.Slug,
                        connector.Catalog.Name),
                    snapshot,
                    cancellationToken)
                .ConfigureAwait(false);
            rowsRead = snapshot.Rows;
            sourceVersion = sourceResult.SourceVersion;
            proposedCheckpoint = sourceResult.ProposedCheckpoint;
            replayKey = sourceResult.ReplayKey;
            if (rowsRead == 0 && connector.ReadMode == DataConnectorReadMode.FullSnapshot)
            {
                throw new JsonSnapshotQualityException(
                    "The connector returned no records, so LakeHold could not infer a replacement schema.");
            }

            await snapshot.SealAsync(cancellationToken).ConfigureAwait(false);
            await using var publicationFence = await connectors.TryBeginPublicationAsync(
                    claim,
                    DateTimeOffset.UtcNow,
                    cancellationToken)
                .ConfigureAwait(false)
                ?? throw new DataConnectorLeaseLostException(
                    "The connector lease expired before publication could begin.");
            if (rowsRead > 0)
            {
                var quality = new JsonSnapshotQualityPolicy(
                    connector.MinimumRows,
                    connector.RequiredColumns(),
                    connector.NotNullColumns());
                var schemaBehavior = (DataConnectorSchemaBehavior)(int)connector.SchemaPolicy;
                published = connector.ReadMode == DataConnectorReadMode.Incremental
                    ? await lakehouse.UpsertJsonDeltaAsync(
                            connector.Tenant.Slug,
                            connector.Catalog.Name,
                            connector.Name,
                            connector.Id,
                            snapshot.Path,
                            connector.TargetSchema,
                            connector.TargetTable,
                            connector.TargetProvisioned,
                            connector.KeyColumns(),
                            quality,
                            schemaBehavior,
                            cancellationToken)
                        .ConfigureAwait(false)
                    : await lakehouse.ReplaceJsonSnapshotAsync(
                            connector.Tenant.Slug,
                            connector.Catalog.Name,
                            connector.Name,
                            connector.Id,
                            snapshot.Path,
                            connector.TargetSchema,
                            connector.TargetTable,
                            connector.TargetProvisioned,
                            quality,
                            schemaBehavior,
                            cancellationToken)
                        .ConfigureAwait(false);
            }
            qualityPassed = true;

            await publicationFence.CompleteAsync(
                    DateTimeOffset.UtcNow,
                    rowsRead,
                    published?.RowsPublished ?? 0,
                    sourceVersion,
                    proposedCheckpoint,
                    replayKey,
                    targetPublished: published is not null,
                    CancellationToken.None)
                .ConfigureAwait(false);

            RecordMetrics(connector, startedAt, LakeholdTelemetry.OutcomeSuccess, rowsRead);
            activity?.SetTag(LakeholdTelemetry.RowsKey, published?.RowsPublished ?? 0);
            return new ConnectorExecutionResult(
                claim.RunId,
                "succeeded",
                rowsRead,
                published?.RowsPublished ?? 0,
                sourceVersion,
                null);
        }
        catch (DataConnectorLeaseLostException ex)
        {
            rowsRead = snapshot?.Rows ?? rowsRead;
            sourceVersion ??= snapshot?.SourceVersion;
            var error = Sanitize(ex, connector);
            RecordMetrics(connector, startedAt, LakeholdTelemetry.OutcomeError, rowsRead);
            activity?.SetStatus(ActivityStatusCode.Error, error);
            ConnectorLog.RefreshFailed(logger, connector.Id, connector.Name, error);
            return new ConnectorExecutionResult(
                claim.RunId,
                "claim-lost",
                rowsRead,
                published?.RowsPublished ?? 0,
                sourceVersion,
                error,
                ConnectorExecutionFailureKind.ClaimConflict);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            rowsRead = snapshot?.Rows ?? rowsRead;
            sourceVersion ??= snapshot?.SourceVersion;
            qualityPassed = ex is JsonSnapshotQualityException ? false : qualityPassed;
            var error = Sanitize(ex, connector);
            if (published is null)
            {
                try
                {
                    await connectors.CompleteFailureAsync(
                            claim,
                            DateTimeOffset.UtcNow,
                            rowsRead,
                            sourceVersion,
                            qualityPassed,
                            error,
                            CancellationToken.None,
                            proposedCheckpoint,
                            replayKey)
                        .ConfigureAwait(false);
                }
                catch (DataConnectorLeaseLostException)
                {
                    error = "The connector claim expired before the failure could be recorded.";
                }
            }
            else
            {
                error = "The target was published, but durable completion could not be confirmed.";
            }

            RecordMetrics(connector, startedAt, LakeholdTelemetry.OutcomeError, rowsRead);
            activity?.SetStatus(ActivityStatusCode.Error, error);
            ConnectorLog.RefreshFailed(logger, connector.Id, connector.Name, error);
            return new ConnectorExecutionResult(
                claim.RunId,
                published is null ? "failed" : "published-unconfirmed",
                rowsRead,
                published?.RowsPublished ?? 0,
                sourceVersion,
                error,
                published is not null
                    ? ConnectorExecutionFailureKind.PublicationState
                    : ex switch
                    {
                        JsonSnapshotQualityException => ConnectorExecutionFailureKind.Quality,
                        JsonSnapshotTargetConflictException => ConnectorExecutionFailureKind.TargetConflict,
                        ConnectorScratchCapacityException => ConnectorExecutionFailureKind.Capacity,
                        _ => ConnectorExecutionFailureKind.SourceOrImport,
                    });
        }
        catch (OperationCanceledException)
        {
            rowsRead = snapshot?.Rows ?? rowsRead;
            sourceVersion ??= snapshot?.SourceVersion;
            const string error = "The connector refresh was cancelled.";
            if (published is null)
            {
                try
                {
                    await connectors.CompleteFailureAsync(
                            claim,
                            DateTimeOffset.UtcNow,
                            rowsRead,
                            sourceVersion,
                            qualityPassed,
                            error,
                            CancellationToken.None,
                            proposedCheckpoint,
                            replayKey)
                        .ConfigureAwait(false);
                }
                catch (DataConnectorLeaseLostException)
                {
                    // A newer claimant owns the durable state and must not be overwritten.
                }
            }
            RecordMetrics(connector, startedAt, LakeholdTelemetry.OutcomeError, rowsRead);
            throw;
        }
        finally
        {
            LakeholdTelemetry.ConnectorWorkersActive.Add(-1);
        }
    }

    private static void RecordMetrics(DataConnector connector, long startedAt, string outcome, long rows)
    {
        var tags = new TagList
        {
            { "lakehold.connector.kind", connector.Kind.ToString().ToLowerInvariant() },
            { LakeholdTelemetry.OutcomeKey, outcome },
        };
        LakeholdTelemetry.ConnectorRuns.Add(1, tags);
        LakeholdTelemetry.ConnectorDuration.Record(Stopwatch.GetElapsedTime(startedAt).TotalSeconds, tags);
        LakeholdTelemetry.ConnectorRows.Record(rows, tags);
    }

    private static string Sanitize(Exception exception, DataConnector connector)
    {
        var message = exception switch
        {
            JsonSnapshotQualityException => exception.Message,
            JsonSnapshotImportException => exception.Message,
            JsonSnapshotTargetConflictException => exception.Message,
            ConnectorScratchCapacityException => exception.Message,
            InvalidDataException => exception.Message,
            DataConnectorLeaseLostException => exception.Message,
            _ => "The connector source could not be read or published.",
        };
        return message.Replace(connector.EndpointUrl, "<connector endpoint>", StringComparison.Ordinal);
    }

    private static string BuildNodeId()
    {
        var machine = Environment.MachineName;
        if (machine.Length > 80)
        {
            machine = machine[..80];
        }

        return $"{machine}-{Guid.NewGuid():N}";
    }
}

internal sealed partial class ConnectorLog
{
    [LoggerMessage(
        EventId = 4300,
        Level = LogLevel.Warning,
        Message = "Connector {ConnectorId} ({ConnectorName}) refresh failed: {SafeError}")]
    public static partial void RefreshFailed(
        ILogger logger,
        int connectorId,
        string connectorName,
        string safeError);

    [LoggerMessage(
        EventId = 4301,
        Level = LogLevel.Warning,
        Message = "Connector {ConnectorId} ({ConnectorName}) scratch cleanup failed with {FailureType}")]
    public static partial void ScratchCleanupFailed(
        ILogger logger,
        int connectorId,
        string connectorName,
        string failureType);
}

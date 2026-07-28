using Lakehold.Engine.Execution;

namespace Lakehold.Api;

/// <summary>Request to execute a statement.</summary>
/// <param name="Sql">The statement to run.</param>
public sealed record ExecuteRequest(string Sql);

/// <summary>A column in a query response.</summary>
public sealed record ColumnDto(string Name, string DataType, string ClrType);

/// <summary>A query response.</summary>
/// <param name="Columns">Column schema in ordinal order.</param>
/// <param name="Rows">Rows aligned to <paramref name="Columns"/> by ordinal.</param>
/// <param name="Truncated">Whether the row ceiling cut the result short.</param>
/// <param name="ElapsedMilliseconds">Server-side execution time.</param>
/// <param name="RowsAffected">
///     Rows changed by a statement whose outcome is a count — <c>INSERT</c>, <c>UPDATE</c>,
///     <c>DELETE</c>, <c>MERGE</c> — and null for anything else. Null and zero differ: null means the
///     statement does not report a count, zero means a DML statement matched nothing.
/// </param>
public sealed record QueryResponse(
    IReadOnlyList<ColumnDto> Columns,
    IReadOnlyList<object?[]> Rows,
    bool Truncated,
    double ElapsedMilliseconds,
    long? RowsAffected)
{
    /// <summary>Maps the engine result onto the transport contract.</summary>
    public static QueryResponse From(QueryResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return new QueryResponse(
            [.. result.Columns.Select(c => new ColumnDto(c.Name, c.DataType, c.ClrType))],
            result.Rows,
            result.Truncated,
            result.Elapsed.TotalMilliseconds,
            result.RowsAffected);
    }
}

/// <summary>A reusable, catalog-scoped query definition.</summary>
/// <param name="Revision">Optimistic authoring revision.</param>
/// <param name="PublishedRevision">Revision currently exposed by the view, or null when unpublished.</param>
public sealed record SavedQueryDto(
    int Id,
    string Name,
    string? Description,
    string Sql,
    int Revision,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    int? CreatedByTokenId,
    int? UpdatedByTokenId,
    string? PublishedSchema,
    string? PublishedViewName,
    int? PublishedRevision,
    DateTimeOffset? PublishedUtc);

/// <summary>Request to save the current SQL as a reusable query.</summary>
public sealed record CreateSavedQueryRequest(string Name, string? Description, string Sql);

/// <summary>Request to replace a saved-query definition at an expected revision.</summary>
public sealed record UpdateSavedQueryRequest(
    int Revision,
    string Name,
    string? Description,
    string Sql);

/// <summary>Request to publish one saved-query revision as a catalog view.</summary>
public sealed record PublishSavedQueryRequest(int Revision, string Schema, string ViewName);

/// <summary>A tenant, as returned by the API.</summary>
public sealed record TenantDto(string Slug, string DisplayName, IReadOnlyList<CatalogDto> Catalogs);

/// <summary>The effective access the current browser has to the workbench.</summary>
/// <param name="Mode"><c>open</c>, <c>authenticated</c>, or <c>demo</c>.</param>
/// <param name="Role">Effective tenant role, lower-case for direct display and client comparison.</param>
/// <param name="ReadOnly">Whether catalogs are attached without write access.</param>
public sealed record AccessDto(string Mode, string Role, bool ReadOnly);

/// <summary>Request to provision a tenant. Instance scope.</summary>
/// <param name="Slug">URL-safe key. Reserved value <c>admin</c> is refused — it collides with the instance-token prefix.</param>
public sealed record CreateTenantRequest(string Slug, string DisplayName);

/// <summary>Request to provision a catalog under a tenant. Instance scope.</summary>
/// <param name="Name">Bare SQL identifier; it reaches <c>ATTACH</c>, which cannot be parameterised.</param>
/// <param name="DataPath">
///     Root for Parquet data. Null derives a local path under the node's data root; a value may be a
///     local path or an object-store URI (<c>s3://</c>, <c>gs://</c>, <c>az://</c>).
/// </param>
/// <param name="ReadOnly">Attach the catalog without write access.</param>
public sealed record CreateCatalogRequest(
    string Name,
    string? DataPath = null,
    bool ReadOnly = false,
    string? StorageProfile = null);

/// <summary>Request to mint a tenant-scoped API token. Returned once at creation.</summary>
/// <param name="Name">Human-facing label. Not a secret and not an identifier.</param>
/// <param name="ReadOnly">Whether the token produces a read-only catalog attachment.</param>
/// <param name="CatalogName">Optional least-privilege narrowing to one catalog in the tenant.</param>
/// <param name="ExpiresUtc">Optional expiry; a token past this instant is refused.</param>
/// <param name="Role">
///     <c>owner</c>, <c>editor</c>, or <c>reader</c>. Defaults to <c>owner</c>, which is what a token
///     minted before roles existed effectively was. A <c>reader</c> is read-only regardless of
///     <paramref name="ReadOnly"/>.
/// </param>
public sealed record CreateTokenRequest(
    string Name,
    bool ReadOnly = false,
    string? CatalogName = null,
    DateTimeOffset? ExpiresUtc = null,
    string? Role = null);

/// <summary>A freshly minted token. The <see cref="Token"/> is shown once and is never recoverable.</summary>
public sealed record CreatedTokenDto(int Id, string Name, string Token);

/// <summary>Token metadata, as listed by the API. Never carries the secret.</summary>
public sealed record ApiTokenDto(
    int Id,
    string Name,
    string Scope,
    string Role,
    string? CatalogName,
    bool ReadOnly,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? ExpiresUtc,
    DateTimeOffset? RevokedUtc,
    DateTimeOffset? LastUsedUtc);

/// <summary>A catalog, as returned by the API.</summary>
/// <remarks>
///     Deliberately omits <c>MetadataSource</c> and <c>StorageSecretName</c>. The former can be a
///     PostgreSQL connection string and the latter names a credential; neither belongs in a
///     response the browser receives.
/// </remarks>
public sealed record CatalogDto(
    string Name,
    string DataPath,
    bool IsReadOnly,
    string MetadataKind,
    string StorageKind,
    string? StorageProfile);

/// <summary>A column in the schema explorer.</summary>
public sealed record SchemaColumnDto(string Name, string DataType, bool IsNullable);

/// <summary>A table in the schema explorer.</summary>
public sealed record SchemaTableDto(string Name, string Kind, IReadOnlyList<SchemaColumnDto> Columns);

/// <summary>A schema in the schema explorer.</summary>
public sealed record SchemaDto(string Name, IReadOnlyList<SchemaTableDto> Tables);

/// <summary>A catalog snapshot.</summary>
public sealed record SnapshotDto(long SnapshotId, DateTimeOffset CommittedAt, long SchemaVersion, string? CommitMessage);

/// <summary>Request to plan or apply a table-data restore from a snapshot.</summary>
/// <param name="Table">Base table whose rows should return to the selected snapshot.</param>
/// <param name="Schema">Schema containing <paramref name="Table"/>.</param>
/// <param name="Apply">False to return a read-only plan; true to commit the reviewed restore.</param>
/// <param name="ExpectedCurrentSnapshotId">
///     Current snapshot returned by the reviewed plan. Required for apply so an intervening write
///     cannot silently invalidate the operator's row and schema review.
/// </param>
public sealed record RestoreTableRequest(
    string Table,
    string Schema = "main",
    bool Apply = false,
    long? ExpectedCurrentSnapshotId = null);

/// <summary>A read-only plan or committed table-data restore.</summary>
public sealed record TableRestoreDto(
    string Schema,
    string Table,
    long SnapshotId,
    long CurrentSnapshotId,
    long CurrentRowCount,
    long HistoricalRowCount,
    IReadOnlyList<string> RestoredColumns,
    IReadOnlyList<string> CurrentOnlyColumns,
    IReadOnlyList<string> HistoricalOnlyColumns,
    bool DryRun);

/// <summary>One table's physical footprint in the storage view.</summary>
/// <param name="RowCount">
///     Live rows, as <c>SELECT count(*)</c> would report: merge-on-read deletes subtracted, rows
///     still inlined in the metadata catalog included.
/// </param>
/// <param name="InlinedRows">
///     Rows committed but not yet written to Parquet. The only thing distinguishing a table whose
///     data is entirely inlined from an empty one — both report zero files.
/// </param>
/// <param name="AverageFileSizeBytes">Mean bytes per data file, or null when there are no files.</param>
/// <param name="NeedsFlush">
///     Whether <c>flush</c> has work to do. Advisory, and the reason the Flush button is no longer
///     a guess.
/// </param>
/// <param name="NeedsCompaction">
///     Whether the table has drifted into the small-file problem — more than one file, averaging
///     below the catalog's <c>target_file_size</c> or the deployment's advisory floor. Advisory only.
/// </param>
public sealed record TableStorageDto(
    string SchemaName,
    string TableName,
    long RowCount,
    long InlinedRows,
    long FileCount,
    long FileSizeBytes,
    long DeleteFileCount,
    long DeleteFileSizeBytes,
    long? AverageFileSizeBytes,
    bool NeedsFlush,
    bool NeedsCompaction);

/// <summary>A catalog's storage footprint.</summary>
/// <param name="TargetFileSizeBytes">
///     The catalog's configured <c>target_file_size</c>, or null when it has never been set. Null is
///     reported rather than guessed — DuckLake's built-in default is not exposed anywhere — and
///     <paramref name="AdvisoryFileSizeBytes"/> is what the advisory actually used.
/// </param>
/// <param name="AdvisoryFileSizeBytes">
///     The threshold <c>NeedsCompaction</c> was computed against, so a caller can see the basis of
///     the advice rather than having to trust it.
/// </param>
public sealed record CatalogStorageDto(
    IReadOnlyList<TableStorageDto> Tables,
    long? TargetFileSizeBytes,
    long AdvisoryFileSizeBytes);

/// <summary>One Parquet data file in the table-detail panel.</summary>
/// <param name="DeleteFile">
///     The merge-on-read delete file paired to this data file, or null when it has none.
/// </param>
public sealed record DataFileDto(
    string DataFile,
    long DataFileSizeBytes,
    string? DeleteFile,
    long? DeleteFileSizeBytes);

/// <summary>A table's data files at one snapshot.</summary>
/// <param name="SnapshotId">The snapshot read, or null for the current one.</param>
/// <param name="Truncated">Whether the list stops short of the table's real file count.</param>
public sealed record TableFilesDto(
    string SchemaName,
    string TableName,
    long? SnapshotId,
    bool Truncated,
    IReadOnlyList<DataFileDto> Files);

/// <summary>A logical column in table detail.</summary>
public sealed record TableDetailColumnDto(string Name, string DataType, bool IsNullable);

/// <summary>One key in a DuckLake partition specification.</summary>
public sealed record PartitionKeyDto(int Position, string ColumnName, string Transform);

/// <summary>A partition specification and the snapshot interval in which it applies.</summary>
public sealed record PartitionSpecDto(
    long PartitionId,
    long BeginSnapshot,
    long? EndSnapshot,
    IReadOnlyList<PartitionKeyDto> Keys);

/// <summary>One table or view's logical, physical, and partition detail.</summary>
public sealed record TableDetailDto(
    string SchemaName,
    string TableName,
    string Kind,
    IReadOnlyList<TableDetailColumnDto> Columns,
    TableStorageDto? Storage,
    IReadOnlyList<PartitionSpecDto> PartitionSpecs,
    long? TargetFileSizeBytes,
    long AdvisoryFileSizeBytes);

/// <summary>Live summary statistics for one logical column.</summary>
public sealed record ColumnProfileDto(
    string Name,
    string DataType,
    long RowCount,
    long NullCount,
    string? Minimum,
    string? Maximum,
    string? ApproxDistinct,
    string? Mean,
    string? StandardDeviation,
    string? FirstQuartile,
    string? Median,
    string? ThirdQuartile);

/// <summary>All columns in one table or view, profiled at one snapshot.</summary>
public sealed record TableProfileDto(
    string SchemaName,
    string TableName,
    long? SnapshotId,
    long RowCount,
    IReadOnlyList<ColumnProfileDto> Columns);

/// <summary>One bounded frequency or range bucket.</summary>
public sealed record DistributionBucketDto(
    string Label,
    string? LowerBound,
    string? UpperBound,
    long Count);

/// <summary>A bounded distribution for one column.</summary>
public sealed record ColumnDistributionDto(
    string SchemaName,
    string TableName,
    string ColumnName,
    string DataType,
    long? SnapshotId,
    string Kind,
    long NullCount,
    bool Truncated,
    IReadOnlyList<DistributionBucketDto> Buckets);

/// <summary>Outcome of a maintenance operation.</summary>
/// <param name="DryRun">
///     True when the operation only reported what it would do. Destructive operations default to
///     this; the caller must pass <c>?apply=true</c> to commit.
/// </param>
public sealed record MaintenanceDto(string Operation, string Detail, double ElapsedMilliseconds, bool DryRun);

/// <summary>An entry in the query history panel.</summary>
/// <param name="TokenId">The credential that ran the statement, or null for pre-auth history.</param>
/// <param name="TokenName">
///     The label of that credential when it still exists, for a readable audit trail; null when the
///     run was anonymous or the token has since been deleted.
/// </param>
public sealed record QueryRunDto(
    int Id,
    string CatalogName,
    string Sql,
    DateTimeOffset StartedUtc,
    double ElapsedMilliseconds,
    int RowCount,
    bool Succeeded,
    string? Error,
    int? TokenId,
    string? TokenName);

/// <summary>A backup generation available to restore.</summary>
/// <param name="Complete">
///     False when the generation has no manifest — it died partway through and restoring it could
///     silently omit deletions.
/// </param>
public sealed record BackupGenerationDto(
    string Generation,
    DateTimeOffset? CreatedUtc,
    long? SnapshotId,
    int TableCount,
    bool Complete);

/// <summary>Request to rebuild a catalog from a backup.</summary>
/// <param name="Generation">Generation to restore, or null for the newest complete one.</param>
/// <param name="TargetMetadataPath">
///     Where to write the rebuilt catalog. Must not already exist — restore never overwrites.
/// </param>
public sealed record RestoreRequest(string? Generation, string TargetMetadataPath);

/// <summary>Outcome of a restore.</summary>
public sealed record RestoreResponse(string MetadataPath, string Generation, int TablesRestored, long RowsRestored);

/// <summary>A recent scheduled maintenance run.</summary>
public sealed record ScheduledRunDto(
    string Job,
    string Tenant,
    string Catalog,
    DateTimeOffset StartedUtc,
    double ElapsedMilliseconds,
    bool Succeeded,
    string Detail);

/// <summary>Request to write a verified eject bundle.</summary>
/// <param name="IncludeHistory">
///     Whether to also copy the metadata catalog so snapshots and time travel survive the export.
///     The data half is reader-agnostic without it; history requires the catalog.
/// </param>
public sealed record EjectRequest(bool IncludeHistory = false);

/// <summary>Outcome of an eject.</summary>
/// <param name="Verified">
///     True when every table's independent re-read matched the catalog's row count. Always true on
///     success — a mismatch fails the request instead.
/// </param>
/// <param name="DigestDeferred">
///     True when per-file digests were skipped because the bundle is on an object store.
/// </param>
public sealed record EjectResponse(
    string Location,
    int TableCount,
    long TotalRows,
    bool Verified,
    bool DigestDeferred,
    bool IsSigned,
    bool IncludesHistory);

/// <summary>An attested table inside an eject bundle.</summary>
public sealed record EjectedTableDto(
    string Schema,
    string Table,
    long RowCount,
    string? Sha256,
    long? Bytes);

/// <summary>An eject bundle available on disk.</summary>
/// <param name="Complete">False when the bundle has no manifest — it died partway and is untrusted.</param>
public sealed record EjectBundleDto(
    string Bundle,
    DateTimeOffset? CreatedUtc,
    long? SnapshotId,
    bool IncludesHistory,
    bool IsSigned,
    bool Complete,
    IReadOnlyList<EjectedTableDto> Tables);

/// <summary>A page of row-level changes from the pull CDC surface.</summary>
public sealed record ChangePageDto(
    string Schema,
    string Table,
    long FromSnapshot,
    long ToSnapshot,
    bool Truncated,
    IReadOnlyList<ChangeDto> Changes);

/// <summary>One row-level change.</summary>
/// <param name="ChangeType">
///     <c>insert</c>, <c>delete</c>, <c>update_preimage</c>, or <c>update_postimage</c>.
/// </param>
public sealed record ChangeDto(
    long SnapshotId,
    long RowId,
    string ChangeType,
    IReadOnlyDictionary<string, object?> Row);

/// <summary>Request to create a change subscription.</summary>
/// <param name="EndpointUrl">HTTP or HTTPS endpoint the signed payloads are posted to.</param>
/// <param name="Secret">
///     Shared secret used to HMAC-sign every delivery. Write-only: it is never returned by any
///     endpoint after creation.
/// </param>
/// <param name="Table">Table to watch, or null to watch every base table in the catalog.</param>
/// <param name="Schema">Schema of <paramref name="Table"/>. Defaults to <c>main</c>.</param>
public sealed record CreateSubscriptionRequest(
    string EndpointUrl,
    string Secret,
    string? Table = null,
    string Schema = "main");

/// <summary>A change subscription, as returned by the API.</summary>
/// <remarks>
///     Deliberately omits the signing secret: it is write-only. Delivery state is included because a
///     subscription you cannot observe is a subscription you do not trust.
/// </remarks>
public sealed record SubscriptionDto(
    int Id,
    string Catalog,
    string Schema,
    string? Table,
    string EndpointUrl,
    bool Active,
    long LastDeliveredSnapshot,
    int ConsecutiveFailures,
    DateTimeOffset? LastAttemptUtc,
    string? LastError,
    DateTimeOffset CreatedUtc);

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
    long? RowsAffected);

/// <summary>A tenant, as returned by the API.</summary>
public sealed record TenantDto(string Slug, string DisplayName, IReadOnlyList<CatalogDto> Catalogs);

/// <summary>A catalog, as returned by the API.</summary>
/// <remarks>
///     Deliberately omits <c>MetadataSource</c> and <c>StorageSecretName</c>. The former can be a
///     PostgreSQL connection string and the latter names a credential; neither belongs in a
///     response the browser receives.
/// </remarks>
public sealed record CatalogDto(string Name, string DataPath, bool IsReadOnly);

/// <summary>A column in the schema explorer.</summary>
public sealed record SchemaColumnDto(string Name, string DataType, bool IsNullable);

/// <summary>A table in the schema explorer.</summary>
public sealed record SchemaTableDto(string Name, string Kind, IReadOnlyList<SchemaColumnDto> Columns);

/// <summary>A schema in the schema explorer.</summary>
public sealed record SchemaDto(string Name, IReadOnlyList<SchemaTableDto> Tables);

/// <summary>A catalog snapshot.</summary>
public sealed record SnapshotDto(long SnapshotId, DateTimeOffset CommittedAt, long SchemaVersion, string? CommitMessage);

/// <summary>Outcome of a maintenance operation.</summary>
/// <param name="DryRun">
///     True when the operation only reported what it would do. Destructive operations default to
///     this; the caller must pass <c>?apply=true</c> to commit.
/// </param>
public sealed record MaintenanceDto(string Operation, string Detail, double ElapsedMilliseconds, bool DryRun);

/// <summary>An entry in the query history panel.</summary>
public sealed record QueryRunDto(
    int Id,
    string CatalogName,
    string Sql,
    DateTimeOffset StartedUtc,
    double ElapsedMilliseconds,
    int RowCount,
    bool Succeeded,
    string? Error);

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

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
public sealed record QueryResponse(
    IReadOnlyList<ColumnDto> Columns,
    IReadOnlyList<object?[]> Rows,
    bool Truncated,
    double ElapsedMilliseconds);

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

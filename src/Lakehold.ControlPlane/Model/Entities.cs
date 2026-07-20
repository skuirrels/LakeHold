using Lakehold.Engine.Catalog;

namespace Lakehold.ControlPlane.Model;

/// <summary>
///     An isolation boundary: an organisation, team, or environment. A tenant owns catalogs, and
///     a query always executes in exactly one tenant's context.
/// </summary>
public sealed class Tenant
{
    public int Id { get; set; }

    /// <summary>Stable URL-safe key used in API routes.</summary>
    public required string Slug { get; set; }

    public required string DisplayName { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }

    public ICollection<LakeCatalog> Catalogs { get; } = [];
}

/// <summary>
///     A DuckLake catalog belonging to a tenant. This is the control-plane record; the engine
///     turns it into a <see cref="CatalogDescriptor"/> when it attaches a compute session.
/// </summary>
public sealed class LakeCatalog
{
    public int Id { get; set; }

    public int TenantId { get; set; }

    /// <summary>
    ///     The name the catalog is attached as, and what tenants write in SQL. Constrained to a
    ///     bare SQL identifier because it reaches <c>ATTACH</c>, which cannot be parameterised.
    /// </summary>
    public required string Name { get; set; }

    public CatalogMetadataKind MetadataKind { get; set; } = CatalogMetadataKind.LocalFile;

    /// <summary>
    ///     File path, or a PostgreSQL connection string for a shared metadata catalog.
    /// </summary>
    /// <remarks>
    ///     A PostgreSQL value is a credential. Production deployments should store a secret
    ///     reference here and resolve it at attach time rather than persisting the password —
    ///     see <see cref="StorageSecretName"/>.
    /// </remarks>
    public required string MetadataSource { get; set; }

    /// <summary>Root URI for Parquet data files. Local path, or <c>s3://</c>, <c>gs://</c>, <c>az://</c>.</summary>
    public required string DataPath { get; set; }

    /// <summary>
    ///     Name of a DuckDB secret granting access to <see cref="DataPath"/>, created during session
    ///     start-up. Only the name is persisted; the credential itself never reaches this table.
    /// </summary>
    public string? StorageSecretName { get; set; }

    public bool IsReadOnly { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }

    public Tenant Tenant { get; set; } = null!;

    /// <summary>Projects this record into the descriptor the engine attaches.</summary>
    public CatalogDescriptor ToDescriptor() => new(
        Name,
        MetadataKind,
        MetadataSource,
        DataPath,
        StorageSecretName,
        IsReadOnly);
}

/// <summary>A named, reusable query.</summary>
public sealed class SavedQuery
{
    public int Id { get; set; }

    public int TenantId { get; set; }

    public required string Name { get; set; }

    public string? Description { get; set; }

    public required string Sql { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }

    public DateTimeOffset UpdatedUtc { get; set; }

    public Tenant Tenant { get; set; } = null!;
}

/// <summary>
///     One executed statement. Doubles as the audit log and as the source for the UI's history
///     panel, so it records both the outcome and the failure reason.
/// </summary>
public sealed class QueryRun
{
    public int Id { get; set; }

    public int TenantId { get; set; }

    public required string CatalogName { get; set; }

    public required string Sql { get; set; }

    public DateTimeOffset StartedUtc { get; set; }

    public double ElapsedMilliseconds { get; set; }

    public int RowCount { get; set; }

    public bool Succeeded { get; set; }

    /// <summary>Failure message when <see cref="Succeeded"/> is false.</summary>
    public string? Error { get; set; }

    public Tenant Tenant { get; set; } = null!;
}

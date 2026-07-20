namespace Lakehold.Engine.Catalog;

/// <summary>
///     Where a tenant's DuckLake metadata lives.
/// </summary>
public enum CatalogMetadataKind
{
    /// <summary>A local DuckDB file. Single-node deployments and development.</summary>
    LocalFile,

    /// <summary>A PostgreSQL database. Required for multi-writer and HA deployments.</summary>
    Postgres,
}

/// <summary>
///     A read-only catalog mounted alongside a tenant's own.
/// </summary>
/// <param name="CatalogName">Alias the catalog is attached as, and referenced by in SQL.</param>
/// <param name="MetadataSource">Path to the catalog's DuckLake metadata.</param>
public sealed record AttachedCatalog(string CatalogName, string MetadataSource);

/// <summary>
///     Everything the engine needs to attach one tenant's lakehouse catalog.
/// </summary>
/// <param name="CatalogName">
///     The identifier the catalog is attached as, and the name tenants write in SQL. Must be a
///     bare identifier — see <see cref="SqlIdentifier"/>.
/// </param>
/// <param name="MetadataKind">Where the DuckLake metadata is stored.</param>
/// <param name="MetadataSource">
///     File path for <see cref="CatalogMetadataKind.LocalFile"/>, or the name of the DuckLake
///     profile secret for <see cref="CatalogMetadataKind.Postgres"/>.
/// </param>
/// <param name="MetadataSecretName">
///     Name of the DuckDB <c>postgres</c> secret holding the metadata database's credentials.
///     Required for <see cref="CatalogMetadataKind.Postgres"/>, where catalog backup has to reach
///     the metadata tables directly. Referencing the secret by name is what keeps the password out
///     of this record, out of options, and out of any statement that could reach a log.
/// </param>
/// <param name="DataPath">Root URI under which DuckLake writes Parquet data files.</param>
/// <param name="MetadataSchema">
///     Schema holding the DuckLake metadata tables, when it is not the default for
///     <paramref name="MetadataKind"/>. Only catalog backup needs this: it reads the metadata tables
///     directly rather than through DuckLake. Null selects the default — <c>main</c> for a local
///     file, <c>public</c> for PostgreSQL.
/// </param>
/// <param name="SecretName">
///     Name of a DuckDB secret created during session initialisation that grants access to
///     <paramref name="DataPath"/>. Null for local filesystem data paths.
/// </param>
/// <param name="ReadOnly">Attach the catalog without write access.</param>
/// <param name="AdditionalCatalogs">
///     Read-only catalogs mounted alongside this one — shared reference data, or a partner's
///     share. Always attached read-only, so a share can never be written through.
/// </param>
public sealed record CatalogDescriptor(
    string CatalogName,
    CatalogMetadataKind MetadataKind,
    string MetadataSource,
    string DataPath,
    string? SecretName = null,
    bool ReadOnly = false,
    IReadOnlyList<AttachedCatalog>? AdditionalCatalogs = null,
    string? MetadataSchema = null,
    string? MetadataSecretName = null)
{
    /// <summary>Read-only catalogs mounted alongside this one. Never null.</summary>
    public IReadOnlyList<AttachedCatalog> AdditionalCatalogs { get; init; } = AdditionalCatalogs ?? [];

    /// <summary>
    ///     Schema the DuckLake metadata tables live in, resolved to the kind's default when unset.
    /// </summary>
    public string ResolvedMetadataSchema => MetadataSchema
        ?? MetadataKind switch
        {
            CatalogMetadataKind.Postgres => "public",
            _ => "main",
        };
}

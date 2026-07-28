using System.Data.Common;
using DuckDB.EFCoreProvider.Infrastructure;
using Lakehold.Engine.Catalog;

namespace Lakehold.Engine.Execution;

/// <summary>
///     Adds deployment-resolved credentials to a new in-process DuckDB session before DuckLake is
///     attached. Implementations must keep credentials out of descriptors and logs.
/// </summary>
public interface IDucklingSessionConfigurator
{
    void Configure(CatalogDescriptor catalog, DuckDBDbContextOptionsBuilder options);

    /// <summary>
    ///     Removes bootstrap-only credentials after DuckLake has attached, before the session can
    ///     execute tenant SQL.
    /// </summary>
    Task SecureAfterAttachAsync(
        CatalogDescriptor catalog,
        DbConnection connection,
        CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    ///     Re-enables metadata credentials for one LakeHold-owned operation while the session gate
    ///     excludes tenant SQL.
    /// </summary>
    Task EnablePrivilegedMetadataAccessAsync(
        CatalogDescriptor catalog,
        DbConnection connection,
        CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Removes credentials enabled by <see cref="EnablePrivilegedMetadataAccessAsync"/>.</summary>
    Task DisablePrivilegedMetadataAccessAsync(
        CatalogDescriptor catalog,
        DbConnection connection,
        CancellationToken cancellationToken) => Task.CompletedTask;
}

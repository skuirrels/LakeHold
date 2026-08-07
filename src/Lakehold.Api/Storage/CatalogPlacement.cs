using System.Diagnostics.CodeAnalysis;
using Lakehold.Engine.Catalog;
using Lakehold.Engine.Configuration;

namespace Lakehold.Api.Storage;

/// <summary>Where a catalog's Parquet goes, and which profile authenticates against it.</summary>
/// <param name="Derived">
///     Whether the path came from the deployment's roots rather than from an explicit request. The
///     browser shows a preview differently in each case: a derived path moves when the tenant or
///     catalog name is edited, an explicit one does not.
/// </param>
internal readonly record struct CatalogPlacementResult(
    string DataPath,
    ParquetStorageKind Kind,
    string? StorageProfile,
    bool Derived);

/// <summary>
///     The single place that turns a tenant, a catalog name, and an optional requested path and
///     profile into a validated placement.
/// </summary>
/// <remarks>
///     <para>
///         Extracted so catalog creation and the storage-resolve preview cannot disagree. A preview
///         that applied different rules from the create it precedes would be worse than no preview:
///         it would show an operator a path the next request refuses, or accept one that creation
///         later derives differently.
///     </para>
///     <para>
///         Deliberately a pure function of configuration. It reads no control-plane row and touches
///         no filesystem or bucket, so the preview endpoint can call it without creating a directory,
///         an object, a metadata schema, or a catalog record. The two checks that genuinely need the
///         database — a duplicate catalog name, and a data path already assigned to another catalog —
///         stay in catalog creation, which is where they can be enforced authoritatively.
///     </para>
/// </remarks>
internal static class CatalogPlacement
{
    internal static bool TryResolve(
        LakehouseOptions options,
        string tenantSlug,
        string catalogName,
        string? requestedDataPath,
        string? requestedStorageProfile,
        out CatalogPlacementResult placement,
        [NotNullWhen(false)] out string? error)
    {
        ArgumentNullException.ThrowIfNull(options);

        placement = default;
        var derived = string.IsNullOrWhiteSpace(requestedDataPath);

        // Guard the inputs CatalogStorageNamespace.Under would throw on. Catalog creation validates
        // the name before it gets here, but the preview is called while an operator is still typing
        // one, and an ArgumentException reaching a route is a 500 for what is really a 400.
        if (derived)
        {
            if (!SqlIdentifier.IsValid(catalogName))
            {
                error = "A catalog name that is a bare SQL identifier is required to derive a data path.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(tenantSlug)
                || tenantSlug.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
            {
                error = "A tenant slug of ASCII letters, digits, hyphens, and underscores is required "
                    + "to derive a data path.";
                return false;
            }
        }

        var dataPath = derived
            ? CatalogStorageNamespace.Under(options.DataRoot, tenantSlug, catalogName)
            : requestedDataPath!;

        var storageKind = StorageLocation.KindOf(dataPath);
        if (storageKind is null)
        {
            error = "DataPath must be a local path or use s3://, gs://, gcs://, az://, azure://, or abfss://.";
            return false;
        }

        // Blank counts as "not specified", not as "no profile". A form posts an empty string for a
        // field nobody touched, and treating that as an explicit choice would refuse a request the
        // caller meant to leave at the deployment default. Narrower than the previous null check,
        // which let an empty string through to be reported as a missing profile.
        var storageProfile = string.IsNullOrWhiteSpace(requestedStorageProfile)
            ? options.DefaultStorageProfile
            : requestedStorageProfile;

        if (storageKind != ParquetStorageKind.Local)
        {
            if (string.IsNullOrWhiteSpace(storageProfile))
            {
                error = $"A storage profile is required for {storageKind} Parquet storage.";
                return false;
            }

            if (!options.StorageProfiles.TryGetValue(storageProfile, out var profile))
            {
                error = $"Storage profile '{storageProfile}' is not configured.";
                return false;
            }

            if (profile.Kind != storageKind)
            {
                error = $"Storage profile '{storageProfile}' is {profile.Kind}, but DataPath requires {storageKind}.";
                return false;
            }
        }
        else if (!string.IsNullOrWhiteSpace(storageProfile))
        {
            // Reached both by naming a profile against a local path and by leaving a deployment
            // default profile in place while choosing one. Neither can attach, so neither is silently
            // dropped: an operator who meant to use the bucket needs to see that the path is local.
            error = "A local DataPath must not select an object-storage profile.";
            return false;
        }

        placement = new CatalogPlacementResult(
            dataPath,
            storageKind.Value,
            storageKind == ParquetStorageKind.Local ? null : storageProfile,
            derived);
        error = null;
        return true;
    }
}

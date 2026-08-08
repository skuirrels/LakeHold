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
        // caller meant to leave at the deployment default.
        var chosenProfile = string.IsNullOrWhiteSpace(requestedStorageProfile)
            ? null
            : requestedStorageProfile;
        string? storageProfile = null;

        if (storageKind != ParquetStorageKind.Local)
        {
            // The deployment default is consulted only here, because that is all it is for:
            // "the profile selected when a remote data path does not name one explicitly".
            // Applying it to a local path as well, and then refusing the result, made an ordinary
            // deployment impossible to use — a local data root alongside a configured bucket profile
            // for individually placed catalogs could not create a catalog at its own default
            // location, and the refusal named a profile the caller had never mentioned.
            storageProfile = chosenProfile ?? options.DefaultStorageProfile;
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
        else if (chosenProfile is not null)
        {
            // Still refused when the caller *asked* for one. That is a real mistake worth naming:
            // the profile cannot attach a local path, and silently dropping it would leave an
            // operator who meant to use the bucket believing they had.
            error = "A local DataPath must not select an object-storage profile.";
            return false;
        }

        // Null for a local path by construction: nothing above can assign a profile to one.
        placement = new CatalogPlacementResult(dataPath, storageKind.Value, storageProfile, derived);
        error = null;
        return true;
    }
}

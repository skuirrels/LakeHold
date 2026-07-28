namespace Lakehold.Engine.Catalog;

/// <summary>Builds tenant-qualified durable locations for one catalog.</summary>
public static class CatalogStorageNamespace
{
    /// <summary>
    ///     Resolves <paramref name="root"/> to a tenant/catalog prefix. Legacy descriptors without a
    ///     tenant key retain their historical catalog-only layout.
    /// </summary>
    public static string Under(string root, CatalogDescriptor catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return Under(root, catalog.TenantKey, catalog.CatalogName);
    }

    /// <summary>Resolves <paramref name="root"/> to a tenant/catalog prefix.</summary>
    public static string Under(string root, string tenantKey, string catalogName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _ = SqlIdentifier.Quote(catalogName, nameof(catalogName));

        if (string.IsNullOrWhiteSpace(tenantKey))
        {
            return StorageLocation.Combine(root, catalogName);
        }

        if (tenantKey.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
        {
            throw new ArgumentException(
                "Tenant storage keys may contain only ASCII letters, digits, hyphens, and underscores.",
                nameof(tenantKey));
        }

        return StorageLocation.Combine(root, tenantKey, catalogName);
    }
}

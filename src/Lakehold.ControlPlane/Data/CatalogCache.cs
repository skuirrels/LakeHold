using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Lakehold.Engine.Catalog;
using Lakehold.Engine.Telemetry;

namespace Lakehold.ControlPlane.Data;

/// <summary>A tenant catalog resolved from the control plane, ready to attach a compute session.</summary>
/// <param name="Descriptor">Everything the engine needs to attach the catalog.</param>
/// <param name="TenantId">The owning tenant, carried so the audit trail need not re-resolve it.</param>
public sealed record ResolvedCatalog(CatalogDescriptor Descriptor, int TenantId);

/// <summary>
///     Node-wide cache of resolved tenant catalogs.
/// </summary>
/// <remarks>
///     <para>
///         Every statement resolves its catalog through the control plane before it can run, and that
///         record changes rarely. Re-reading it from the database on every query is a per-statement
///         round trip for a value that is effectively constant, so the resolved descriptor is cached
///         here and the read is paid once per catalog rather than once per query.
///     </para>
///     <para>
///         A singleton, because <see cref="LakehouseService"/> is request-scoped and the cache must
///         outlive any one request. Entries are dropped through <see cref="Invalidate"/> whenever a
///         catalog's configuration changes — the same seam that evicts the catalog's warm session so
///         the next query reattaches with the new settings. Only successful resolutions are cached;
///         a miss throws and caches nothing, so a catalog created after start-up resolves cleanly.
///     </para>
/// </remarks>
public sealed class CatalogCache
{
    // Separates the two key parts. NUL cannot occur in a URL-safe slug or in a bare SQL identifier,
    // so no pair of distinct (tenant, catalog) values can produce the same key. A printable
    // separator would instead lean on the slug never containing one, which nothing enforces.
    private const char KeySeparator = '\0';

    private readonly ConcurrentDictionary<string, ResolvedCatalog> _entries = new(StringComparer.Ordinal);

    /// <summary>Returns the cached resolution for a tenant's catalog, if one is present.</summary>
    public bool TryGet(string tenantSlug, string catalogName, [MaybeNullWhen(false)] out ResolvedCatalog resolved)
    {
        var hit = _entries.TryGetValue(Key(tenantSlug, catalogName), out resolved);

        // Tagged hit/miss only — the tenant and catalog would give this counter one time series per
        // tenant, and the point of the metric is the aggregate rate, not who missed.
        LakeholdTelemetry.CatalogCacheRequests.Add(
            1,
            new KeyValuePair<string, object?>(
                LakeholdTelemetry.ResultKey,
                hit ? LakeholdTelemetry.ResultHit : LakeholdTelemetry.ResultMiss));

        return hit;
    }

    /// <summary>Records a resolution so later queries for the same catalog skip the database read.</summary>
    public void Set(string tenantSlug, string catalogName, ResolvedCatalog resolved)
        => _entries[Key(tenantSlug, catalogName)] = resolved;

    /// <summary>
    ///     Drops every cached entry for a catalog name, across all tenants that hold it.
    /// </summary>
    /// <remarks>
    ///     Rarely the right thing to call on its own: the warm session in <c>DucklingPool</c> caches
    ///     the same configuration in attached form, so dropping the descriptor alone leaves queries
    ///     running against the old attachment. Prefer
    ///     <see cref="LakehouseService.ForgetCatalogAsync"/>, which drops both in the correct order.
    /// </remarks>
    public void Invalidate(string catalogName)
    {
        foreach (var pair in _entries)
        {
            if (string.Equals(pair.Value.Descriptor.CatalogName, catalogName, StringComparison.Ordinal))
            {
                _entries.TryRemove(pair.Key, out _);
            }
        }
    }

    private static string Key(string tenantSlug, string catalogName)
        => string.Concat(tenantSlug, KeySeparator, catalogName);
}

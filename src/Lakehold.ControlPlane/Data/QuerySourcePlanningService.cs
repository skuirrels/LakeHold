using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lakehold.Querying;
using Lakehold.Engine.Telemetry;

namespace Lakehold.ControlPlane.Data;

/// <summary>Builds a credential-free schema snapshot and delegates source translation.</summary>
public sealed class QuerySourcePlanningService(
    LakehouseService lakehouse,
    IQuerySourcePlanner planners,
    QueryPlanValidator? validator = null,
    QueryPlanCache? cache = null)
{
    private readonly QueryPlanValidator _validator = validator ?? new QueryPlanValidator(lakehouse);
    private readonly QueryPlanCache _cache = cache ?? new QueryPlanCache();

    public Task<IReadOnlyList<QueryLanguageDescriptor>> GetLanguagesAsync(CancellationToken cancellationToken)
        => planners.GetLanguagesAsync(cancellationToken);

    public async Task<QueryLanguageStarter> CreateStarterAsync(
        string tenant,
        string catalog,
        string language,
        CancellationToken cancellationToken)
    {
        language = string.IsNullOrWhiteSpace(language) ? "sql" : language.Trim();
        var catalogSchema = string.Equals(language, "sql", StringComparison.Ordinal)
            ? new QueryCatalogSchema("sql", [])
            : await GetCatalogSchemaAsync(tenant, catalog, cancellationToken).ConfigureAwait(false);
        return await planners.CreateStarterAsync(language, catalogSchema, cancellationToken).ConfigureAwait(false);
    }

    public async Task<QueryPlan> PlanAsync(
        string tenant,
        string catalog,
        string language,
        string source,
        CancellationToken cancellationToken)
    {
        language = string.IsNullOrWhiteSpace(language) ? "sql" : language.Trim();
        if (string.IsNullOrWhiteSpace(source))
        {
            throw new ArgumentException("Query source is required.", nameof(source));
        }

        if (string.Equals(language, "sql", StringComparison.Ordinal))
        {
            return await planners.PlanAsync(
                language,
                new QueryPlanningRequest(source, "sql", []),
                cancellationToken).ConfigureAwait(false);
        }

        var catalogSchema = await GetCatalogSchemaAsync(tenant, catalog, cancellationToken).ConfigureAwait(false);

        if (_cache.TryGet(language, source, catalogSchema.SchemaFingerprint, out var cachedPlan))
        {
            LakeholdTelemetry.QueryPlanCacheRequests.Add(1,
                new KeyValuePair<string, object?>(LakeholdTelemetry.ResultKey, LakeholdTelemetry.ResultHit));
            return cachedPlan;
        }

        LakeholdTelemetry.QueryPlanCacheRequests.Add(1,
            new KeyValuePair<string, object?>(LakeholdTelemetry.ResultKey, LakeholdTelemetry.ResultMiss));
        var startedAt = TimeProvider.System.GetTimestamp();

        var plan = await planners.PlanAsync(
            language,
            new QueryPlanningRequest(source, catalogSchema.SchemaFingerprint, catalogSchema.Tables),
            cancellationToken).ConfigureAwait(false);
        await _validator.ValidateAsync(
            tenant,
            catalog,
            plan,
            catalogSchema.SchemaFingerprint,
            cancellationToken).ConfigureAwait(false);
        LakeholdTelemetry.QueryPlanningDuration.Record(
            TimeProvider.System.GetElapsedTime(startedAt).TotalSeconds,
            new KeyValuePair<string, object?>("lakehold.query.language", language));
        _cache.Set(language, source, catalogSchema.SchemaFingerprint, plan);
        return plan;
    }

    public async Task<QueryCatalogSchema> GetCatalogSchemaAsync(
        string tenant,
        string catalog,
        CancellationToken cancellationToken)
    {
        var schemas = await lakehouse.GetSchemasAsync(tenant, catalog, cancellationToken).ConfigureAwait(false);
        var tables = schemas.SelectMany(schema => schema.Tables.Select(table => new QueryTableSchema(
                schema.Name,
                table.Name,
                table.Kind,
                [.. table.Columns.Select(column => new QueryColumnSchema(
                    column.Name,
                    column.DataType,
                    column.IsNullable))])))
            .OrderBy(table => table.Schema, StringComparer.Ordinal)
            .ThenBy(table => table.Name, StringComparer.Ordinal)
            .ToArray();

        return new QueryCatalogSchema(Fingerprint(tables), tables);
    }

    public async Task<string> GetCatalogSchemaFingerprintAsync(
        string tenant,
        string catalog,
        CancellationToken cancellationToken)
        => (await GetCatalogSchemaAsync(tenant, catalog, cancellationToken).ConfigureAwait(false)).SchemaFingerprint;

    private static string Fingerprint(IReadOnlyList<QueryTableSchema> tables)
    {
        var canonical = JsonSerializer.Serialize(tables);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}

/// <summary>
/// Node-local compilation cache. Schema is still read from the shared control plane on every request,
/// so a schema change produces a new key on every node and can never reuse a stale executable plan.
/// </summary>
public sealed class QueryPlanCache
{
    private const int Capacity = 256;
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Entry> _entries = new();

    public bool TryGet(string language, string source, string fingerprint, out QueryPlan plan)
    {
        var key = Key(language, source, fingerprint);
        if (_entries.TryGetValue(key, out var entry) && entry.ExpiresUtc > DateTimeOffset.UtcNow)
        {
            plan = entry.Plan;
            return true;
        }

        _entries.TryRemove(key, out _);
        plan = null!;
        return false;
    }

    public void Set(string language, string source, string fingerprint, QueryPlan plan)
    {
        if (_entries.Count >= Capacity)
        {
            foreach (var expired in _entries
                         .Where(pair => pair.Value.ExpiresUtc <= DateTimeOffset.UtcNow)
                         .Take(Math.Max(1, Capacity / 8)))
            {
                _entries.TryRemove(expired.Key, out _);
            }

            if (_entries.Count >= Capacity)
            {
                var victim = _entries.FirstOrDefault();
                if (victim.Key is not null)
                {
                    _entries.TryRemove(victim.Key, out _);
                }
            }
        }

        _entries[Key(language, source, fingerprint)] = new Entry(plan, DateTimeOffset.UtcNow + Lifetime);
    }

    private static string Key(string language, string source, string fingerprint)
    {
        var bytes = Encoding.UTF8.GetBytes(string.Concat(language, "\0", fingerprint, "\0", source));
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private sealed record Entry(QueryPlan Plan, DateTimeOffset ExpiresUtc);
}

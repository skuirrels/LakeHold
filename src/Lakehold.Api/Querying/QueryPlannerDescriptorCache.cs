using System.Collections.Concurrent;
using Lakehold.Querying;

namespace Lakehold.Api.Querying;

/// <summary>
///     The last descriptor each configured planner served, so a planner that goes unhealthy keeps
///     its display name, editor language, and starter in the selector instead of degrading to its
///     raw configured id.
/// </summary>
/// <remarks>
///     A singleton because <see cref="QueryPlannerRegistry"/> is scoped: without somewhere outside
///     the request to remember it, the common failure — a healthy compiler that misses one
///     discovery deadline — would rename the language for exactly the load that noticed.
/// </remarks>
public sealed class QueryPlannerDescriptorCache
{
    private readonly ConcurrentDictionary<string, QueryLanguageDescriptor> _descriptors =
        new(StringComparer.Ordinal);

    /// <summary>The last descriptor this planner served, or null if it has never answered.</summary>
    public QueryLanguageDescriptor? Get(string plannerId)
        => _descriptors.TryGetValue(plannerId, out var descriptor) ? descriptor : null;

    /// <summary>Records a descriptor the host has already accepted as healthy.</summary>
    public void Remember(QueryLanguageDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        _descriptors[descriptor.Id] = descriptor;
    }
}

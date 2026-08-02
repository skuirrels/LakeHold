namespace Lakehold.Api.Querying;

/// <summary>Configured external Workbench query planners.</summary>
public sealed class QueryPlannerOptions
{
    public const string Section = "Lakehold:Querying";

    public List<ExternalQueryPlannerOptions> Planners { get; set; } = [];

    /// <summary>Maximum descriptor, starter, failure, or plan response accepted from a plugin.</summary>
    public int MaxResponseBytes { get; set; } = 4 * 1024 * 1024;
}

public sealed class ExternalQueryPlannerOptions
{
    public required string Id { get; set; }

    public required Uri Endpoint { get; set; }

    public string? SharedSecret { get; set; }
}

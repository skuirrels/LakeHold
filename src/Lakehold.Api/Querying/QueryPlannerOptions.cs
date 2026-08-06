namespace Lakehold.Api.Querying;

/// <summary>Configured external Workbench query planners.</summary>
public sealed class QueryPlannerOptions
{
    public const string Section = "Lakehold:Querying";

    /// <summary>
    ///     The language this process plans itself, which discovery always serves and no planner may
    ///     claim.
    /// </summary>
    public const string BuiltInLanguageId = "sql";

    public List<ExternalQueryPlannerOptions> Planners { get; set; } = [];

    /// <summary>Maximum descriptor, starter, failure, or plan response accepted from a plugin.</summary>
    public int MaxResponseBytes { get; set; } = 4 * 1024 * 1024;

    /// <summary>
    ///     Whether no configured planner has taken <see cref="BuiltInLanguageId"/>.
    /// </summary>
    /// <remarks>
    ///     Uniqueness among planners is not enough, because the built-in language is not one of them.
    ///     A planner sharing its id puts the id in the language list twice, and callers key languages
    ///     by id — the Workbench selector tracks its options that way and treats a repeat as an error,
    ///     so one misconfigured planner would empty the selector rather than add to it.
    /// </remarks>
    public bool LeavesBuiltInLanguageAlone()
        => Planners.TrueForAll(planner =>
            !string.Equals(planner.Id, BuiltInLanguageId, StringComparison.Ordinal));
}

public sealed class ExternalQueryPlannerOptions
{
    public required string Id { get; set; }

    public required Uri Endpoint { get; set; }

    public string? SharedSecret { get; set; }
}

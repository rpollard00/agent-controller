namespace AgentController.Application.Results;

/// <summary>
/// Result of a <see cref="ListRunsQuery"/> containing enriched run summaries.
/// </summary>
public sealed record RunListResult
{
    /// <summary>List of run summaries matching the query.</summary>
    public IReadOnlyList<RunSummaryItem> Runs { get; init; } = [];
}

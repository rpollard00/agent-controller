namespace AgentController.Application.Queries;

public sealed record ListPullRequestDiagnosticsQuery
{
    public string? SourceControlEnvironmentKey { get; init; }
    public bool IncludeInactive { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = ManagedPullRequestDiscoveryQuery.DefaultPageSize;
}

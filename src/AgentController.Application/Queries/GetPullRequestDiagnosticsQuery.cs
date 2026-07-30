namespace AgentController.Application.Queries;

public sealed record GetPullRequestDiagnosticsQuery
{
    public string SourceControlEnvironmentKey { get; init; } = string.Empty;
    public string RepositoryKey { get; init; } = string.Empty;
    public string PullRequestId { get; init; } = string.Empty;
}

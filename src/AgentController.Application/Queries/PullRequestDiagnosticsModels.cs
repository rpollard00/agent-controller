using AgentController.Domain;

namespace AgentController.Application.Queries;

public enum PullRequestRequestMatch
{
    None,
    Revival,
    Assistance,
    Both,
}

public enum PullRequestDiagnosticOutcome
{
    NotEligible,
    Eligible,
    AssistanceTakesPrecedence,
}

public sealed record PullRequestDiagnosticOptions
{
    public string RevivalLabel { get; init; } = "agent-rework-requested";
    public string AssistanceLabel { get; init; } = "agent-assistance-requested";
}

public sealed record PullRequestSourceOption
{
    public string Key { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
}

public sealed record PullRequestDiagnosticCheck
{
    public string Code { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public bool Passed { get; init; }
    public string Reason { get; init; } = string.Empty;
}

public sealed record PullRequestDiagnosticSummary
{
    public string PullRequestId { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string? Url { get; init; }
    public string SourceControlEnvironmentKey { get; init; } = string.Empty;
    public string RepositoryKey { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public PullRequestRequestMatch Request { get; init; }
}

public sealed record PullRequestDiagnosticsPage
{
    public IReadOnlyList<PullRequestSourceOption> Sources { get; init; } = [];
    public IReadOnlyList<PullRequestDiagnosticSummary> Items { get; init; } = [];
    public IReadOnlyList<ManagedPullRequestDiscoveryFailure> Failures { get; init; } = [];
    public int Page { get; init; }
    public int PageSize { get; init; }
    public int Total { get; init; }
}

public sealed record PullRequestTrackingState
{
    public ReworkRequestMode RequestMode { get; init; }
    public ReworkFeedbackStatus FeedbackStatus { get; init; }
    public ReworkCycleStatus? CycleStatus { get; init; }
}

public sealed record PullRequestDiagnosticDetail
{
    public string PullRequestId { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string? Url { get; init; }
    public string SourceControlEnvironmentKey { get; init; } = string.Empty;
    public string RepositoryKey { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string SourceBranch { get; init; } = string.Empty;
    public string TargetBranch { get; init; } = string.Empty;
    public IReadOnlyList<string> Labels { get; init; } = [];
    public IReadOnlyList<PullRequestWorkItemReference> LinkedWorkItems { get; init; } = [];
    public PullRequestRequestMatch Request { get; init; }
    public PullRequestDiagnosticOutcome Outcome { get; init; }
    public bool Eligible { get; init; }
    public IReadOnlyList<PullRequestDiagnosticCheck> Checks { get; init; } = [];
    public string RecognizedRevivalLabel { get; init; } = string.Empty;
    public string RecognizedAssistanceLabel { get; init; } = string.Empty;
    public ReviewFeedbackCheckTrace? FeedbackTrace { get; init; }
    public PullRequestTrackingState? Tracking { get; init; }
}

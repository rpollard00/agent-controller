namespace AgentController.Application;

/// <summary>Read-only query for pull requests visible in managed repositories.</summary>
public sealed record ManagedPullRequestDiscoveryQuery
{
    public const int DefaultPageSize = 50;
    public const int MaximumPageSize = 100;

    /// <summary>Optional source-control environment key.</summary>
    public string? SourceControlEnvironmentKey { get; init; }

    /// <summary>Whether completed and abandoned pull requests should be included.</summary>
    public bool IncludeInactive { get; init; }

    /// <summary>One-based result page.</summary>
    public int Page { get; init; } = 1;

    /// <summary>Number of rows per page.</summary>
    public int PageSize { get; init; } = DefaultPageSize;
}

/// <summary>Operator-safe failure for one managed repository.</summary>
public sealed record ManagedPullRequestDiscoveryFailure
{
    public string SourceControlEnvironmentKey { get; init; } = string.Empty;
    public string RepositoryKey { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
}

/// <summary>A deterministic page of managed pull-request discovery results.</summary>
public sealed record ManagedPullRequestDiscoveryPage
{
    public IReadOnlyList<ManagedPullRequestSnapshot> Items { get; init; } = [];
    public IReadOnlyList<ManagedPullRequestDiscoveryFailure> Failures { get; init; } = [];
    public int Page { get; init; }
    public int PageSize { get; init; }
    public int Total { get; init; }
}

/// <summary>
/// Provider-neutral, read-only port for diagnostic discovery of managed pull requests.
/// This path is independent of the polling discovery contract.
/// </summary>
public interface IManagedPullRequestDiagnosticDiscovery
{
    Task<ManagedPullRequestDiscoveryPage> ListAsync(
        ManagedPullRequestDiscoveryQuery query,
        CancellationToken cancellationToken
    );
}

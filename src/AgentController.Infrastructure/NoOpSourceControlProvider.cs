using AgentController.Application;
using AgentController.Domain;

namespace AgentController.Infrastructure;

/// <summary>
/// Deterministic no-op implementation of <see cref="ISourceControlProvider"/>.
/// Returns empty checkouts and not-found status. Suitable for DI seeding
/// before real providers are wired.
/// </summary>
public sealed class NoOpSourceControlProvider : ISourceControlProvider
{
    public Task<RepositoryCheckout> CloneAsync(
        RepositorySpec spec,
        EnvironmentHandle environment,
        CancellationToken cancellationToken
    )
    {
        var checkout = new RepositoryCheckout
        {
            RepoKey = spec.RepoKey,
            LocalPath = string.Empty,
            Branch = spec.DefaultBranch,
            CommitSha = null,
            ClonedAt = DateTimeOffset.UnixEpoch,
        };

        return Task.FromResult(checkout);
    }

    public Task<SourceControlStatus> GetStatusAsync(
        SourceControlRef sourceControlRef,
        CancellationToken cancellationToken
    )
    {
        return Task.FromResult(
            new SourceControlStatus
            {
                Exists = false,
                Branch = sourceControlRef.Branch,
                CommitSha = sourceControlRef.CommitSha,
                PullRequestUrl = null,
                PullRequestStatus = null,
            }
        );
    }
}

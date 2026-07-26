using AgentController.Application;
using AgentController.Domain;

namespace AgentController.Infrastructure;

/// <summary>Pull-request label mutator used when no repository provider is configured.</summary>
internal sealed class NoOpPullRequestLabelMutator : IPullRequestLabelMutator
{
    public Task MutateAsync(
        PullRequestReference pullRequest,
        PullRequestLabelMutation mutation,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(pullRequest);
        ArgumentNullException.ThrowIfNull(mutation);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}

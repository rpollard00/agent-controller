using AgentController.Application;
using AgentController.Domain;

namespace AgentController.Infrastructure;

/// <summary>
/// In-memory label mutation for deterministic offline pull-request workflows.
/// Mutations share state with <see cref="LocalPullRequestDiscovery"/>.
/// </summary>
internal sealed class LocalPullRequestLabelMutator(
    LocalPullRequestState state
) : IPullRequestLabelMutator
{
    public Task MutateAsync(
        PullRequestReference pullRequest,
        PullRequestLabelMutation mutation,
        CancellationToken cancellationToken
    )
    {
        state.MutateLabels(pullRequest, mutation, cancellationToken);
        return Task.CompletedTask;
    }
}

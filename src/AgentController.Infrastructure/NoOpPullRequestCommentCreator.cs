using AgentController.Application;
using AgentController.Domain;

namespace AgentController.Infrastructure;

/// <summary>Pull-request comment creator used when no repository provider is configured.</summary>
internal sealed class NoOpPullRequestCommentCreator : IPullRequestCommentCreator
{
    public Task CreateAsync(
        PullRequestReference pullRequest,
        AssistanceLifecycleCommentRequest request,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(pullRequest);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}

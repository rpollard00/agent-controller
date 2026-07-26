using AgentController.Application;

namespace AgentController.Infrastructure;

/// <summary>
/// Pull-request discovery used when no provider is configured.
/// </summary>
internal sealed class NoOpPullRequestDiscovery : IManagedPullRequestDiscovery
{
    public Task<IReadOnlyList<ManagedPullRequestSnapshot>> ListActiveAsync(
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<ManagedPullRequestSnapshot>>(
            Array.Empty<ManagedPullRequestSnapshot>()
        );
    }
}

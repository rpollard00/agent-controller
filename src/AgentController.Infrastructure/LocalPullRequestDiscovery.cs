using AgentController.Application;
using AgentController.Infrastructure.Options;
using Microsoft.Extensions.Options;

namespace AgentController.Infrastructure;

/// <summary>
/// Configuration-backed pull-request discovery for deterministic offline workflows.
/// Every valid definition represents an active pull request.
/// </summary>
internal sealed class LocalPullRequestDiscovery : IManagedPullRequestDiscovery
{
    private readonly LocalPullRequestState _state;

    public LocalPullRequestDiscovery(IOptionsMonitor<LocalPullRequestOptions> options)
        : this(new LocalPullRequestState(options))
    {
    }

    internal LocalPullRequestDiscovery(LocalPullRequestState state)
    {
        _state = state;
    }

    public Task<IReadOnlyList<ManagedPullRequestSnapshot>> ListActiveAsync(
        CancellationToken cancellationToken
    )
    {
        return Task.FromResult(_state.List(cancellationToken));
    }
}

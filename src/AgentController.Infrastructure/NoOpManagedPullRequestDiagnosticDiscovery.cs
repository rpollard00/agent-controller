using AgentController.Application;

namespace AgentController.Infrastructure;

/// <summary>Empty diagnostics used when managed pull-request enumeration is unavailable.</summary>
internal sealed class NoOpManagedPullRequestDiagnosticDiscovery
    : IManagedPullRequestDiagnosticDiscovery
{
    public Task<ManagedPullRequestDiscoveryPage> ListAsync(
        ManagedPullRequestDiscoveryQuery query,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new ManagedPullRequestDiscoveryPage
        {
            Page = query.Page,
            PageSize = query.PageSize,
        });
    }
}

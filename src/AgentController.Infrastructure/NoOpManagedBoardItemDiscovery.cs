using AgentController.Application;

namespace AgentController.Infrastructure;

/// <summary>
/// Empty diagnostic board discovery used when the configured work-source provider does not
/// support managed board enumeration.
/// </summary>
internal sealed class NoOpManagedBoardItemDiscovery : IManagedBoardItemDiscovery
{
    public Task<ManagedBoardItemDiscoveryPage> ListAsync(
        ManagedBoardItemDiscoveryQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(new ManagedBoardItemDiscoveryPage
        {
            Page = query.Page,
            PageSize = query.PageSize,
            Total = 0,
        });
    }

    public Task<ManagedBoardItemSnapshot?> GetAsync(
        ManagedBoardItemDiscoveryItemQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<ManagedBoardItemSnapshot?>(null);
    }
}

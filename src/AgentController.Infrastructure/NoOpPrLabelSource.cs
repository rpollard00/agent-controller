using AgentController.Application;

namespace AgentController.Infrastructure;

/// <summary>
/// No-op <see cref="IPrLabelSource"/> that always returns an empty label list.
/// Used as the default when no feedback provider is configured so the
/// filter pipeline can still be resolved (marker gate fails-closed for all PRs).
/// </summary>
internal sealed class NoOpPrLabelSource : IPrLabelSource
{
    public Task<IReadOnlyList<PrLabel>> GetLabelsAsync(
        PrUnderTest pr,
        CancellationToken cancellationToken)
    {
        return Task.FromResult<IReadOnlyList<PrLabel>>(Array.Empty<PrLabel>());
    }
}

using AgentController.Application;

namespace AgentController.Infrastructure;

/// <summary>
/// No-op <see cref="IFeedbackSource"/> that always returns an empty signal list.
/// Used when <c>feedback:provider</c> is <c>"None"</c> so the feedback pipeline
/// can still be resolved without crashing on <c>GetRequiredService&lt;IFeedbackSource&gt;</c>.
/// </summary>
internal sealed class NoOpFeedbackSource : IFeedbackSource
{
    public Task<IReadOnlyList<ReworkSignal>> PollAsync(
        FeedbackQuery query,
        CancellationToken cancellationToken)
    {
        return Task.FromResult<IReadOnlyList<ReworkSignal>>(Array.Empty<ReworkSignal>());
    }
}

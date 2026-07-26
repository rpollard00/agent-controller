using AgentController.Domain;

namespace AgentController.Application.Queries;

/// <summary>Matches a feedback observation to the cycle materialized from it.</summary>
internal static class ReworkTrackingMatcher
{
    public static bool IsSameMaterialization(ReworkFeedback feedback, ReworkCycle cycle)
    {
        if (!string.IsNullOrWhiteSpace(feedback.CorrelationId)
            && !string.IsNullOrWhiteSpace(cycle.CorrelationId))
        {
            return string.Equals(
                feedback.CorrelationId,
                cycle.CorrelationId,
                StringComparison.Ordinal
            );
        }

        if (feedback.RequestMode != cycle.RequestMode
            || !string.Equals(
                feedback.FeedbackBundleId,
                cycle.FeedbackBundleId,
                StringComparison.Ordinal
            ))
        {
            return false;
        }

        if (feedback.PullRequest.HasCanonicalIdentity && cycle.PullRequest.HasCanonicalIdentity)
        {
            return string.Equals(
                feedback.PullRequest.CanonicalKey,
                cycle.PullRequest.CanonicalKey,
                StringComparison.Ordinal
            );
        }

        // Legacy cycles did not persist the provider PR ID. Their globally unique
        // feedback bundle remains the only available materialization key.
        return string.IsNullOrWhiteSpace(feedback.PullRequestId)
            || string.IsNullOrWhiteSpace(cycle.PullRequest.PullRequestId)
            || string.Equals(
                feedback.PullRequestId,
                cycle.PullRequest.PullRequestId,
                StringComparison.Ordinal
            );
    }
}

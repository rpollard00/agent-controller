using AgentController.Domain;

namespace AgentController.Application.Services;

/// <summary>Configured pull-request labels used by the assistance lifecycle.</summary>
public sealed record AssistancePullRequestLabels
{
    public string Requested { get; init; } = "agent-assistance-requested";
    public string InProgress { get; init; } = "agent-assistance-in-progress";
    public string RevivalRequested { get; init; } = "agent-rework-requested";
}

/// <summary>Assistance lifecycle milestones that affect pull-request labels.</summary>
public enum AssistanceRunLabelMilestone
{
    RuntimeAccepted,
    BranchPushed,
    Completed,
    Failed,
    NeedsHuman,
    Cancelled,
}

/// <summary>
/// Projects assistance-run progress onto the pull request being continued.
/// Implementations must leave Revival runs unchanged.
/// </summary>
public interface IAssistancePullRequestLabelProjector
{
    Task ProjectAsync(
        AgentRunHandle run,
        AssistanceRunLabelMilestone milestone,
        CancellationToken cancellationToken
    );
}

/// <summary>
/// Resolves the rework cycle associated with a run (including retry lineage) and applies
/// idempotent pull-request label mutations for Assistance cycles only.
/// </summary>
internal sealed class AssistancePullRequestLabelProjector(
    IAgentRunStore runStore,
    IReworkCycleStore reworkCycleStore,
    IPullRequestLabelMutator labelMutator,
    AssistancePullRequestLabels labels
) : IAssistancePullRequestLabelProjector
{
    public async Task ProjectAsync(
        AgentRunHandle run,
        AssistanceRunLabelMilestone milestone,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(run);

        var cycle = await ResolveCycleAsync(run, cancellationToken);
        if (cycle is null)
            return;

        var mutation = BuildMutation(cycle.RequestMode, milestone, labels);
        if (mutation is null)
            return;

        await labelMutator.MutateAsync(cycle.PullRequest, mutation, cancellationToken);
    }

    internal static PullRequestLabelMutation? BuildMutation(
        ReworkRequestMode requestMode,
        AssistanceRunLabelMilestone milestone,
        AssistancePullRequestLabels labels
    )
    {
        if (requestMode != ReworkRequestMode.Assistance)
            return null;

        return milestone switch
        {
            AssistanceRunLabelMilestone.RuntimeAccepted => new PullRequestLabelMutation
            {
                LabelsToAdd = [labels.InProgress],
            },
            AssistanceRunLabelMilestone.BranchPushed
                or AssistanceRunLabelMilestone.Completed => new PullRequestLabelMutation
                {
                    LabelsToRemove =
                    [
                        labels.Requested,
                        labels.InProgress,
                        labels.RevivalRequested,
                    ],
                },
            AssistanceRunLabelMilestone.Failed
                or AssistanceRunLabelMilestone.NeedsHuman
                or AssistanceRunLabelMilestone.Cancelled => new PullRequestLabelMutation
                {
                    LabelsToRemove = [labels.InProgress],
                },
            _ => null,
        };
    }

    private async Task<ReworkCycle?> ResolveCycleAsync(
        AgentRunHandle run,
        CancellationToken cancellationToken
    )
    {
        // A retry run does not consume the cycle again. Walk its persisted lineage so it
        // projects against the same pull request as the original assistance attempt.
        var visitedRunIds = new HashSet<string>(StringComparer.Ordinal);
        AgentRunHandle? lineageRun = run;

        while (lineageRun is not null && visitedRunIds.Add(lineageRun.RunId))
        {
            var consumedCycle = await reworkCycleStore.GetConsumedByRunIdAsync(
                lineageRun.RunId,
                cancellationToken
            );
            if (
                consumedCycle is not null
                && string.Equals(consumedCycle.WorkItemId, run.WorkItemId, StringComparison.Ordinal)
            )
            {
                return consumedCycle;
            }

            lineageRun = string.IsNullOrWhiteSpace(lineageRun.PreviousRunId)
                ? null
                : await runStore.GetByIdAsync(lineageRun.PreviousRunId, cancellationToken);
        }

        // Setup failures can happen before context injection consumes the cycle. Resolving
        // the pending row makes removing an in-progress label safe in that path as well.
        return string.IsNullOrWhiteSpace(run.WorkItemId)
            ? null
            : await reworkCycleStore.GetPendingForWorkItemAsync(
                run.WorkItemId,
                cancellationToken
            );
    }
}

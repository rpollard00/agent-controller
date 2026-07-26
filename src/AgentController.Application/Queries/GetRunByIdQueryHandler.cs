using AgentController.Application.Abstractions;
using AgentController.Application.Results;
using AgentController.Domain;

namespace AgentController.Application.Queries;

/// <summary>
/// Handles <see cref="GetRunByIdQuery"/> by assembling full run detail from
/// multiple stores: agent run, work item, environment, and lifecycle events.
/// Returns null when no run is found so the endpoint can control the 404.
/// </summary>
public sealed class GetRunByIdQueryHandler(
    IAgentRunStore runStore,
    IWorkItemStore workItemStore,
    IEnvironmentStore environmentStore,
    ILifecycleEventStore lifecycleEventStore,
    IReworkCycleStore reworkCycleStore,
    IReworkFeedbackStore reworkFeedbackStore
) : IQueryHandler<GetRunByIdQuery, RunDetailResult?>
{
    private readonly IAgentRunStore _runStore = runStore;
    private readonly IWorkItemStore _workItemStore = workItemStore;
    private readonly IEnvironmentStore _environmentStore = environmentStore;
    private readonly ILifecycleEventStore _lifecycleEventStore = lifecycleEventStore;
    private readonly IReworkCycleStore _reworkCycleStore = reworkCycleStore;
    private readonly IReworkFeedbackStore _reworkFeedbackStore = reworkFeedbackStore;

    public async Task<RunDetailResult?> ExecuteAsync(
        GetRunByIdQuery query,
        CancellationToken cancellationToken
    )
    {
        var run = await _runStore.GetByIdAsync(query.RunId, cancellationToken);
        if (run is null)
        {
            return null;
        }

        // Fetch the associated work item
        WorkCandidate? workItem = null;
        if (!string.IsNullOrWhiteSpace(run.WorkItemId))
        {
            workItem = await _workItemStore.GetByIdAsync(
                run.WorkItemId,
                cancellationToken
            );
        }

        // Fetch the environment if one exists
        EnvironmentHandle? environment = null;
        if (!string.IsNullOrWhiteSpace(run.EnvironmentId))
        {
            environment = await _environmentStore.GetByIdAsync(
                run.EnvironmentId,
                cancellationToken
            );
        }

        // Fetch ordered lifecycle events
        var lifecycleEvents = await _lifecycleEventStore.ListByRunIdAsync(
            query.RunId,
            cancellationToken
        );
        var cycle = await ResolveCycleAsync(run, cancellationToken);
        var feedback = await ResolveFeedbackAsync(cycle, cancellationToken);

        return new RunDetailResult
        {
            RunId = run.RunId,
            WorkItemId = run.WorkItemId,
            WorkItem = workItem,
            EnvironmentId = run.EnvironmentId,
            RuntimeType = run.RuntimeType,
            RuntimeRunId = run.RuntimeRunId,
            Status = run.Status.ToString(),
            BranchName = run.BranchName,
            PullRequestUrl = run.PullRequestUrl,
            ResultSummary = run.ResultSummary,
            StartedAt = run.StartedAt,
            FinishedAt = run.FinishedAt,
            LastHeartbeatAt = run.LastHeartbeatAt,
            Error = run.Error,
            RequestMode = cycle?.RequestMode,
            PullRequest = cycle?.PullRequest,
            CycleNumber = cycle?.CycleNumber,
            AssistanceStoryWorkItemId = feedback?.AssistanceStoryWorkItemId
                ?? (cycle?.RequestMode == ReworkRequestMode.Assistance ? cycle.WorkItemId : null),
            AssistanceStoryExternalId = feedback?.AssistanceStoryExternalId
                ?? (cycle?.RequestMode == ReworkRequestMode.Assistance ? workItem?.ExternalId : null),
            AssistanceStoryUrl = feedback?.AssistanceStoryUrl
                ?? (cycle?.RequestMode == ReworkRequestMode.Assistance ? workItem?.ExternalUrl : null),
            FeedbackStatus = feedback?.Status,
            CycleStatus = cycle?.Status,
            ConsumingRunId = cycle?.NewRunId,
            Environment = environment,
            LifecycleEvents = lifecycleEvents,
            CreatedAt = run.CreatedAt,
            UpdatedAt = run.UpdatedAt,
        };
    }

    private async Task<ReworkCycle?> ResolveCycleAsync(
        AgentRunHandle run,
        CancellationToken cancellationToken
    )
    {
        var visitedRunIds = new HashSet<string>(StringComparer.Ordinal);
        AgentRunHandle? lineageRun = run;
        while (lineageRun is not null && visitedRunIds.Add(lineageRun.RunId))
        {
            var consumedCycle = await _reworkCycleStore.GetConsumedByRunIdAsync(
                lineageRun.RunId,
                cancellationToken
            );
            if (consumedCycle is not null)
            {
                return consumedCycle;
            }

            if (string.IsNullOrWhiteSpace(lineageRun.PreviousRunId))
            {
                break;
            }

            lineageRun = await _runStore.GetByIdAsync(
                lineageRun.PreviousRunId,
                cancellationToken
            );
        }

        return string.IsNullOrWhiteSpace(run.WorkItemId)
            ? null
            : await _reworkCycleStore.GetPendingForWorkItemAsync(
                run.WorkItemId,
                cancellationToken
            );
    }

    private async Task<ReworkFeedback?> ResolveFeedbackAsync(
        ReworkCycle? cycle,
        CancellationToken cancellationToken
    )
    {
        if (cycle is null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(cycle.CorrelationId))
        {
            var correlated = await _reworkFeedbackStore.GetByCorrelationIdAsync(
                cycle.CorrelationId,
                cancellationToken
            );
            if (correlated is not null)
            {
                return correlated;
            }
        }

        var trackedFeedback = await _reworkFeedbackStore.GetTrackedAsync(cancellationToken);
        return trackedFeedback.FirstOrDefault(feedback =>
            ReworkTrackingMatcher.IsSameMaterialization(feedback, cycle)
        );
    }
}

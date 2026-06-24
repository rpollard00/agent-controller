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
    ILifecycleEventStore lifecycleEventStore
) : IQueryHandler<GetRunByIdQuery, RunDetailResult?>
{
    private readonly IAgentRunStore _runStore = runStore;
    private readonly IWorkItemStore _workItemStore = workItemStore;
    private readonly IEnvironmentStore _environmentStore = environmentStore;
    private readonly ILifecycleEventStore _lifecycleEventStore = lifecycleEventStore;

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
            Environment = environment,
            LifecycleEvents = lifecycleEvents,
            CreatedAt = run.CreatedAt,
            UpdatedAt = run.UpdatedAt,
        };
    }
}

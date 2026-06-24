using AgentController.Application.Abstractions;
using AgentController.Application.Results;
using AgentController.Domain;

namespace AgentController.Application.Queries;

/// <summary>
/// Handles <see cref="ListRunsQuery"/> by listing runs from the store
/// and enriching each with work item title/repo key via an N+1 join
/// to <see cref="IWorkItemStore"/>.
/// </summary>
public sealed class ListRunsQueryHandler(
    IAgentRunStore runStore,
    IWorkItemStore workItemStore
) : IQueryHandler<ListRunsQuery, RunListResult>
{
    private readonly IAgentRunStore _runStore = runStore;
    private readonly IWorkItemStore _workItemStore = workItemStore;

    public async Task<RunListResult> ExecuteAsync(
        ListRunsQuery query,
        CancellationToken cancellationToken
    )
    {
        var runs = await _runStore.ListAsync(query, cancellationToken);

        var items = new List<RunSummaryItem>(runs.Count);
        foreach (var run in runs)
        {
            string? workItemTitle = null;
            string? repoKey = null;

            if (!string.IsNullOrWhiteSpace(run.WorkItemId))
            {
                var wi = await _workItemStore.GetByIdAsync(
                    run.WorkItemId,
                    cancellationToken
                );
                if (wi is not null)
                {
                    workItemTitle = wi.Title;
                    repoKey = wi.RepoKey;
                }
            }

            items.Add(new RunSummaryItem
            {
                RunId = run.RunId,
                WorkItemId = run.WorkItemId,
                WorkItemTitle = workItemTitle,
                RepoKey = repoKey,
                Status = run.Status.ToString(),
                StartedAt = run.StartedAt,
                FinishedAt = run.FinishedAt,
                LastHeartbeatAt = run.LastHeartbeatAt,
                CreatedAt = run.CreatedAt,
            });
        }

        return new RunListResult { Runs = items };
    }
}

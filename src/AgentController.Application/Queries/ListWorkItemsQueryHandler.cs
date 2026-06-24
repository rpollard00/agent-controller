using AgentController.Application.Abstractions;
using AgentController.Domain;

namespace AgentController.Application.Queries;

/// <summary>
/// Handles <see cref="ListWorkItemsQuery"/> by delegating to <see cref="IWorkItemStore.ListAsync"/>.
/// </summary>
public sealed class ListWorkItemsQueryHandler(
    IWorkItemStore workItemStore
) : IQueryHandler<ListWorkItemsQuery, IReadOnlyList<WorkCandidate>>
{
    private readonly IWorkItemStore _workItemStore = workItemStore;

    public async Task<IReadOnlyList<WorkCandidate>> ExecuteAsync(
        ListWorkItemsQuery query,
        CancellationToken cancellationToken
    )
    {
        return await _workItemStore.ListAsync(query, cancellationToken);
    }
}

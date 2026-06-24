using AgentController.Application.Abstractions;
using AgentController.Domain;

namespace AgentController.Application.Queries;

/// <summary>
/// Handles <see cref="GetWorkItemByIdQuery"/> by delegating to <see cref="IWorkItemStore.GetByIdAsync"/>.
/// Returns null when no work item is found; the endpoint is responsible for mapping to 404.
/// </summary>
public sealed class GetWorkItemByIdQueryHandler(
    IWorkItemStore workItemStore
) : IQueryHandler<GetWorkItemByIdQuery, WorkCandidate?>
{
    private readonly IWorkItemStore _workItemStore = workItemStore;

    public async Task<WorkCandidate?> ExecuteAsync(
        GetWorkItemByIdQuery query,
        CancellationToken cancellationToken
    )
    {
        return await _workItemStore.GetByIdAsync(query.Id, cancellationToken);
    }
}

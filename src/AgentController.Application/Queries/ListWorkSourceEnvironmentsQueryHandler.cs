using AgentController.Application.Abstractions;
using AgentController.Domain;

namespace AgentController.Application.Queries;

/// <summary>Lists managed work source environments in deterministic store order.</summary>
public sealed class ListWorkSourceEnvironmentsQueryHandler(
    IWorkSourceEnvironmentStore environmentStore
) : IQueryHandler<ListWorkSourceEnvironmentsQuery, IReadOnlyList<WorkSourceEnvironmentProfile>>
{
    private readonly IWorkSourceEnvironmentStore _environmentStore = environmentStore;

    public async Task<IReadOnlyList<WorkSourceEnvironmentProfile>> ExecuteAsync(
        ListWorkSourceEnvironmentsQuery query,
        CancellationToken cancellationToken
    )
    {
        return await _environmentStore.ListAsync(cancellationToken);
    }
}

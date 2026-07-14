using AgentController.Application.Abstractions;
using AgentController.Domain;

namespace AgentController.Application.Queries;

/// <summary>Lists managed Azure DevOps environments in deterministic store order.</summary>
public sealed class ListAzureDevOpsEnvironmentsQueryHandler(
    IWorkSourceEnvironmentStore environmentStore
) : IQueryHandler<ListAzureDevOpsEnvironmentsQuery, IReadOnlyList<WorkSourceEnvironmentProfile>>
{
    private readonly IWorkSourceEnvironmentStore _environmentStore = environmentStore;

    public async Task<IReadOnlyList<WorkSourceEnvironmentProfile>> ExecuteAsync(
        ListAzureDevOpsEnvironmentsQuery query,
        CancellationToken cancellationToken
    )
    {
        return await _environmentStore.ListAsync(cancellationToken);
    }
}

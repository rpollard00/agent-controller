using AgentController.Application.Abstractions;
using AgentController.Domain;

namespace AgentController.Application.Queries;

/// <summary>Lists managed Azure DevOps environments in deterministic store order.</summary>
public sealed class ListAzureDevOpsEnvironmentsQueryHandler(
    IAzureDevOpsEnvironmentStore environmentStore
) : IQueryHandler<ListAzureDevOpsEnvironmentsQuery, IReadOnlyList<AzureDevOpsEnvironmentProfile>>
{
    private readonly IAzureDevOpsEnvironmentStore _environmentStore = environmentStore;

    public async Task<IReadOnlyList<AzureDevOpsEnvironmentProfile>> ExecuteAsync(
        ListAzureDevOpsEnvironmentsQuery query,
        CancellationToken cancellationToken
    )
    {
        return await _environmentStore.ListAsync(cancellationToken);
    }
}

using AgentController.Application.Abstractions;
using AgentController.Domain;

namespace AgentController.Application.Queries;

/// <summary>Lists managed runtime environments in deterministic store order.</summary>
public sealed class ListRuntimeEnvironmentsQueryHandler(IRuntimeEnvironmentStore environmentStore)
    : IQueryHandler<ListRuntimeEnvironmentsQuery, IReadOnlyList<RuntimeEnvironmentProfile>>
{
    private readonly IRuntimeEnvironmentStore _environmentStore = environmentStore;

    public async Task<IReadOnlyList<RuntimeEnvironmentProfile>> ExecuteAsync(
        ListRuntimeEnvironmentsQuery query,
        CancellationToken cancellationToken
    )
    {
        return await _environmentStore.ListAsync(cancellationToken);
    }
}

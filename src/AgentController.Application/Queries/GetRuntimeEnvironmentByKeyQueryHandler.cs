using AgentController.Application.Abstractions;
using AgentController.Application.Results;

namespace AgentController.Application.Queries;

/// <summary>Validates and reads a managed runtime environment by key.</summary>
public sealed class GetRuntimeEnvironmentByKeyQueryHandler(
    IRuntimeEnvironmentStore environmentStore
) : IQueryHandler<GetRuntimeEnvironmentByKeyQuery, RuntimeEnvironmentOperationResult>
{
    private readonly IRuntimeEnvironmentStore _environmentStore = environmentStore;

    public async Task<RuntimeEnvironmentOperationResult> ExecuteAsync(
        GetRuntimeEnvironmentByKeyQuery query,
        CancellationToken cancellationToken
    )
    {
        var key = RuntimeEnvironmentProfileValidation.ValidateAndNormalizeKey(query.Key);
        if (!key.IsValid)
        {
            return RuntimeEnvironmentOperationResult.ValidationFailed(key.Errors);
        }

        var environment = await _environmentStore.GetByKeyAsync(key.Key, cancellationToken);
        return environment is null
            ? RuntimeEnvironmentOperationResult.NotFound(
                $"Runtime environment '{key.Key}' was not found."
            )
            : RuntimeEnvironmentOperationResult.Succeeded(environment);
    }
}

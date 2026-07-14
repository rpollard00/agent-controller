using AgentController.Application.Abstractions;
using AgentController.Application.Results;

namespace AgentController.Application.Queries;

/// <summary>Validates and reads a managed Azure DevOps environment by key.</summary>
public sealed class GetAzureDevOpsEnvironmentByKeyQueryHandler(
    IWorkSourceEnvironmentStore environmentStore
) : IQueryHandler<GetAzureDevOpsEnvironmentByKeyQuery, AzureDevOpsEnvironmentOperationResult>
{
    private readonly IWorkSourceEnvironmentStore _environmentStore = environmentStore;

    public async Task<AzureDevOpsEnvironmentOperationResult> ExecuteAsync(
        GetAzureDevOpsEnvironmentByKeyQuery query,
        CancellationToken cancellationToken
    )
    {
        var key = AzureDevOpsEnvironmentProfileValidation.ValidateAndNormalizeKey(query.Key);
        if (!key.IsValid)
        {
            return AzureDevOpsEnvironmentOperationResult.ValidationFailed(key.Errors);
        }

        var environment = await _environmentStore.GetByKeyAsync(key.Key, cancellationToken);
        return environment is null
            ? AzureDevOpsEnvironmentOperationResult.NotFound(
                $"Azure DevOps environment '{key.Key}' was not found."
            )
            : AzureDevOpsEnvironmentOperationResult.Succeeded(environment);
    }
}

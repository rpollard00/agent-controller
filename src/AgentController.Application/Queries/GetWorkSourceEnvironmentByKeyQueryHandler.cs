using AgentController.Application.Abstractions;
using AgentController.Application.Results;

namespace AgentController.Application.Queries;

/// <summary>Validates and reads a managed work source environment by key.</summary>
public sealed class GetWorkSourceEnvironmentByKeyQueryHandler(
    IWorkSourceEnvironmentStore environmentStore
) : IQueryHandler<GetWorkSourceEnvironmentByKeyQuery, WorkSourceEnvironmentOperationResult>
{
    private readonly IWorkSourceEnvironmentStore _environmentStore = environmentStore;

    public async Task<WorkSourceEnvironmentOperationResult> ExecuteAsync(
        GetWorkSourceEnvironmentByKeyQuery query,
        CancellationToken cancellationToken
    )
    {
        var key = WorkSourceEnvironmentProfileValidation.ValidateAndNormalizeKey(query.Key);
        if (!key.IsValid)
        {
            return WorkSourceEnvironmentOperationResult.ValidationFailed(key.Errors);
        }

        var environment = await _environmentStore.GetByKeyAsync(key.Key, cancellationToken);
        return environment is null
            ? WorkSourceEnvironmentOperationResult.NotFound(
                $"Work source environment '{key.Key}' was not found."
            )
            : WorkSourceEnvironmentOperationResult.Succeeded(environment);
    }
}

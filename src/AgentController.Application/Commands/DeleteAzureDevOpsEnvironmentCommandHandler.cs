using AgentController.Application.Abstractions;
using AgentController.Application.Results;

namespace AgentController.Application.Commands;

/// <summary>
/// Validates and deletes an unreferenced managed Azure DevOps environment profile.
/// </summary>
public sealed class DeleteAzureDevOpsEnvironmentCommandHandler(
    IWorkSourceEnvironmentStore environmentStore,
    IRepositoryStore repositoryStore
) : ICommandHandler<DeleteAzureDevOpsEnvironmentCommand, AzureDevOpsEnvironmentOperationResult>
{
    private readonly IWorkSourceEnvironmentStore _environmentStore = environmentStore;
    private readonly IRepositoryStore _repositoryStore = repositoryStore;

    public async Task<AzureDevOpsEnvironmentOperationResult> HandleAsync(
        DeleteAzureDevOpsEnvironmentCommand command,
        CancellationToken cancellationToken
    )
    {
        var key = AzureDevOpsEnvironmentProfileValidation.ValidateAndNormalizeKey(command.Key);
        if (!key.IsValid)
        {
            return AzureDevOpsEnvironmentOperationResult.ValidationFailed(key.Errors);
        }

        var existing = await _environmentStore.GetByKeyAsync(key.Key, cancellationToken);
        if (existing is null)
        {
            return AzureDevOpsEnvironmentOperationResult.NotFound(
                $"Azure DevOps environment '{key.Key}' was not found."
            );
        }

        var repositories = await _repositoryStore.ListAsync(cancellationToken);
        var referencingRepository = repositories
            .Where(repository =>
                string.Equals(
                    repository.AzureDevOpsEnvironmentKey?.Trim(),
                    key.Key,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            .OrderBy(repository => repository.Key, StringComparer.Ordinal)
            .FirstOrDefault();

        if (referencingRepository is not null)
        {
            return AzureDevOpsEnvironmentOperationResult.Conflict(
                $"Azure DevOps environment '{key.Key}' is referenced by repository '{referencingRepository.Key}'."
            );
        }

        var deleted = await _environmentStore.DeleteAsync(key.Key, cancellationToken);
        return deleted
            ? AzureDevOpsEnvironmentOperationResult.Succeeded()
            : AzureDevOpsEnvironmentOperationResult.NotFound(
                $"Azure DevOps environment '{key.Key}' was not found."
            );
    }
}

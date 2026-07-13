using AgentController.Application.Abstractions;
using AgentController.Application.Results;

namespace AgentController.Application.Commands;

/// <summary>Validates and deletes an unreferenced managed runtime environment profile.</summary>
public sealed class DeleteRuntimeEnvironmentCommandHandler(
    IRuntimeEnvironmentStore environmentStore,
    IRepositoryStore repositoryStore
) : ICommandHandler<DeleteRuntimeEnvironmentCommand, RuntimeEnvironmentOperationResult>
{
    private readonly IRuntimeEnvironmentStore _environmentStore = environmentStore;
    private readonly IRepositoryStore _repositoryStore = repositoryStore;

    public async Task<RuntimeEnvironmentOperationResult> HandleAsync(
        DeleteRuntimeEnvironmentCommand command,
        CancellationToken cancellationToken
    )
    {
        var key = RuntimeEnvironmentProfileValidation.ValidateAndNormalizeKey(command.Key);
        if (!key.IsValid)
        {
            return RuntimeEnvironmentOperationResult.ValidationFailed(key.Errors);
        }

        var existing = await _environmentStore.GetByKeyAsync(key.Key, cancellationToken);
        if (existing is null)
        {
            return RuntimeEnvironmentOperationResult.NotFound(
                $"Runtime environment '{key.Key}' was not found."
            );
        }

        var repositories = await _repositoryStore.ListAsync(cancellationToken);
        var referencingRepository = repositories
            .Where(repository =>
                string.Equals(
                    repository.RuntimeEnvironmentKey?.Trim(),
                    key.Key,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            .OrderBy(repository => repository.Key, StringComparer.Ordinal)
            .FirstOrDefault();

        if (referencingRepository is not null)
        {
            return RuntimeEnvironmentOperationResult.Conflict(
                $"Runtime environment '{key.Key}' is referenced by repository '{referencingRepository.Key}'."
            );
        }

        var deleted = await _environmentStore.DeleteAsync(key.Key, cancellationToken);
        return deleted
            ? RuntimeEnvironmentOperationResult.Succeeded()
            : RuntimeEnvironmentOperationResult.NotFound(
                $"Runtime environment '{key.Key}' was not found."
            );
    }
}

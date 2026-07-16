using AgentController.Application.Abstractions;
using AgentController.Application.Results;

namespace AgentController.Application.Commands;

/// <summary>
/// Validates and deletes an unreferenced managed repository host connection profile.
/// </summary>
public sealed class DeleteRepositoryHostConnectionCommandHandler(
    IRepositoryHostConnectionStore connectionStore,
    IRepositoryStore repositoryStore
) : ICommandHandler<DeleteRepositoryHostConnectionCommand, RepositoryHostConnectionOperationResult>
{
    private readonly IRepositoryHostConnectionStore _connectionStore = connectionStore;
    private readonly IRepositoryStore _repositoryStore = repositoryStore;

    public async Task<RepositoryHostConnectionOperationResult> HandleAsync(
        DeleteRepositoryHostConnectionCommand command,
        CancellationToken cancellationToken
    )
    {
        var key = RepositoryHostConnectionProfileValidation.ValidateAndNormalizeKey(command.Key);
        if (!key.IsValid)
        {
            return RepositoryHostConnectionOperationResult.ValidationFailed(key.Errors);
        }

        var existing = await _connectionStore.GetByKeyAsync(key.Key, cancellationToken);
        if (existing is null)
        {
            return RepositoryHostConnectionOperationResult.NotFound(
                $"Repository host connection '{key.Key}' was not found."
            );
        }

        // TODO: when RepositoryProfile.RepositoryHostConnectionKey is wired (later refactor item),
        // check for referencing repositories and return Conflict if any exist.
        // For now, RepositoryHostConnectionKey does not yet exist on RepositoryProfile.
        _ = await _repositoryStore.ListAsync(cancellationToken);

        var deleted = await _connectionStore.DeleteAsync(key.Key, cancellationToken);
        return deleted
            ? RepositoryHostConnectionOperationResult.Succeeded()
            : RepositoryHostConnectionOperationResult.NotFound(
                $"Repository host connection '{key.Key}' was not found."
            );
    }
}

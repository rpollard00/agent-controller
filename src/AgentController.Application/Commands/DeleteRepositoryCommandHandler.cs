using AgentController.Application.Abstractions;
using AgentController.Application.Results;

namespace AgentController.Application.Commands;

/// <summary>Validates and deletes a managed repository profile by key.</summary>
public sealed class DeleteRepositoryCommandHandler(IRepositoryStore repositoryStore)
    : ICommandHandler<DeleteRepositoryCommand, RepositoryOperationResult>
{
    private readonly IRepositoryStore _repositoryStore = repositoryStore;

    public async Task<RepositoryOperationResult> HandleAsync(
        DeleteRepositoryCommand command,
        CancellationToken cancellationToken
    )
    {
        var key = RepositoryProfileValidation.ValidateAndNormalizeKey(command.Key);
        if (!key.IsValid)
        {
            return RepositoryOperationResult.ValidationFailed(key.Errors);
        }

        var deleted = await _repositoryStore.DeleteAsync(key.Key, cancellationToken);
        return deleted
            ? RepositoryOperationResult.Succeeded()
            : RepositoryOperationResult.NotFound($"Repository '{key.Key}' was not found.");
    }
}

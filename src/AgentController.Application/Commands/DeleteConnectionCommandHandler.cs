using AgentController.Application.Abstractions;
using AgentController.Application.Results;

namespace AgentController.Application.Commands;

/// <summary>Validates and deletes a managed connection profile.</summary>
public sealed class DeleteConnectionCommandHandler(
    IConnectionStore connectionStore
) : ICommandHandler<DeleteConnectionCommand, ConnectionOperationResult>
{
    private readonly IConnectionStore _connectionStore = connectionStore;

    public async Task<ConnectionOperationResult> HandleAsync(
        DeleteConnectionCommand command,
        CancellationToken cancellationToken
    )
    {
        var key = ConnectionProfileValidation.ValidateAndNormalizeKey(command.Key);
        if (!key.IsValid)
        {
            return ConnectionOperationResult.ValidationFailed(key.Errors);
        }

        var existing = await _connectionStore.GetByKeyAsync(key.Key, cancellationToken);
        if (existing is null)
        {
            return ConnectionOperationResult.NotFound(
                $"Connection '{key.Key}' was not found."
            );
        }

        var deleted = await _connectionStore.DeleteAsync(key.Key, cancellationToken);
        return deleted
            ? ConnectionOperationResult.Succeeded()
            : ConnectionOperationResult.NotFound(
                $"Connection '{key.Key}' was not found."
            );
    }
}

using AgentController.Application.Abstractions;
using AgentController.Application.Results;
using AgentController.Domain;
using AgentController.Domain.Secrets;

namespace AgentController.Application.Commands;

/// <summary>Validates, normalizes, and updates a managed connection profile.</summary>
public sealed class UpdateConnectionCommandHandler(
    IConnectionStore connectionStore,
    ISecretManager secretManager
) : ICommandHandler<UpdateConnectionCommand, ConnectionOperationResult>
{
    private readonly IConnectionStore _connectionStore = connectionStore;
    private readonly ISecretManager _secretManager = secretManager;

    public async Task<ConnectionOperationResult> HandleAsync(
        UpdateConnectionCommand command,
        CancellationToken cancellationToken
    )
    {
        var keyValidation = ConnectionProfileValidation.ValidateAndNormalizeKey(command.Key);
        if (!keyValidation.IsValid)
        {
            return ConnectionOperationResult.ValidationFailed(keyValidation.Errors);
        }

        var existing = await _connectionStore.GetByKeyAsync(
            keyValidation.Key,
            cancellationToken
        );
        if (existing is null)
        {
            return ConnectionOperationResult.NotFound(
                $"Connection '{keyValidation.Key}' was not found."
            );
        }

        if (command.Profile is null)
        {
            return ConnectionOperationResult.ValidationFailed(
                new Dictionary<string, string[]>
                {
                    ["profile"] = ["A connection profile is required."],
                }
            );
        }

        var validation = ConnectionProfileValidation.ValidateAndNormalize(command.Profile);
        if (!validation.IsValid)
        {
            return ConnectionOperationResult.ValidationFailed(validation.Errors);
        }

        var credentialErrors = await ConnectionProfileValidation.ValidateCredentialTypeAsync(
            validation.Profile,
            _secretManager,
            cancellationToken
        );
        if (credentialErrors.Count > 0)
        {
            return ConnectionOperationResult.ValidationFailed(credentialErrors);
        }

        // Preserve the original key and timestamps.
        var profile = validation.Profile with
        {
            Key = keyValidation.Key,
            CreatedAt = existing.CreatedAt,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        var updated = await _connectionStore.UpdateAsync(profile, cancellationToken);
        return updated
            ? ConnectionOperationResult.Succeeded(profile)
            : ConnectionOperationResult.NotFound(
                $"Connection '{keyValidation.Key}' was not found."
            );
    }
}

using AgentController.Application.Abstractions;
using AgentController.Application.Results;
using AgentController.Domain;
using AgentController.Domain.Secrets;

namespace AgentController.Application.Commands;

/// <summary>Validates, normalizes, and creates a managed connection profile.</summary>
public sealed class CreateConnectionCommandHandler(
    IConnectionStore connectionStore,
    ISecretManager secretManager
) : ICommandHandler<CreateConnectionCommand, ConnectionOperationResult>
{
    private readonly IConnectionStore _connectionStore = connectionStore;
    private readonly ISecretManager _secretManager = secretManager;

    public async Task<ConnectionOperationResult> HandleAsync(
        CreateConnectionCommand command,
        CancellationToken cancellationToken
    )
    {
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

        var now = DateTimeOffset.UtcNow;
        var profile = validation.Profile with { CreatedAt = now, UpdatedAt = now };
        var created = await _connectionStore.CreateAsync(profile, cancellationToken);

        return created
            ? ConnectionOperationResult.Succeeded(profile)
            : ConnectionOperationResult.Conflict(
                $"Connection '{profile.Key}' already exists."
            );
    }
}

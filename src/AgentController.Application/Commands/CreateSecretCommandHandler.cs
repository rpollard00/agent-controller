using AgentController.Application.Abstractions;
using AgentController.Application.Results;
using AgentController.Domain.Secrets;

namespace AgentController.Application.Commands;

/// <summary>Creates a new named secret via ISecretManager.</summary>
public sealed class CreateSecretCommandHandler(ISecretManager secretManager)
    : ICommandHandler<CreateSecretCommand, CreateSecretResult>
{
    private readonly ISecretManager _secretManager = secretManager;

    public async Task<CreateSecretResult> HandleAsync(
        CreateSecretCommand command,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(command.Name))
        {
            return CreateSecretResult.ValidationFailed(
                new Dictionary<string, string[]>
                {
                    ["name"] = ["Secret name is required."],
                }
            );
        }

        if (command.Name.Length > 256)
        {
            return CreateSecretResult.ValidationFailed(
                new Dictionary<string, string[]>
                {
                    ["name"] = ["Secret name must be 256 characters or fewer."],
                }
            );
        }

        var validationErrors = new Dictionary<string, string[]>();

        if (command.Payload is PersonalAccessTokenPayload patPayload)
        {
            if (string.IsNullOrEmpty(patPayload.Value))
            {
                validationErrors["payload.value"] = ["PAT value is required."];
            }
        }
        else if (command.Payload is SshKeyPayload sshPayload)
        {
            if (string.IsNullOrEmpty(sshPayload.PrivateKey))
            {
                validationErrors["payload.privateKey"] = ["SSH private key is required."];
            }

            if (string.IsNullOrEmpty(sshPayload.PublicKey))
            {
                validationErrors["payload.publicKey"] = ["SSH public key is required."];
            }
        }
        else
        {
            validationErrors["payload.type"] = ["Unsupported secret payload type."];
        }

        if (validationErrors.Count > 0)
        {
            return CreateSecretResult.ValidationFailed(validationErrors);
        }

        var created = await _secretManager.CreateAsync(
            command.Name,
            command.Payload,
            cancellationToken
        );

        return created
            ? CreateSecretResult.Succeeded(command.Name)
            : CreateSecretResult.Conflict($"Secret '{command.Name}' already exists.");
    }
}

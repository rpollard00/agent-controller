using AgentController.Application.Abstractions;
using AgentController.Application.Results;
using AgentController.Domain.Secrets;

namespace AgentController.Application.Commands;

/// <summary>Creates a new version of an existing secret via ISecretManager.</summary>
public sealed class CreateSecretVersionCommandHandler(ISecretManager secretManager)
    : ICommandHandler<CreateSecretVersionCommand, CreateSecretVersionResult>
{
    private readonly ISecretManager _secretManager = secretManager;

    public async Task<CreateSecretVersionResult> HandleAsync(
        CreateSecretVersionCommand command,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(command.Name))
        {
            return CreateSecretVersionResult.ValidationFailed(
                new Dictionary<string, string[]>
                {
                    ["name"] = ["Secret name is required."],
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
            return CreateSecretVersionResult.ValidationFailed(validationErrors);
        }

        var version = await _secretManager.CreateVersionAsync(
            command.Name,
            command.Payload,
            cancellationToken
        );

        return version.HasValue
            ? CreateSecretVersionResult.Succeeded(command.Name, version.Value)
            : CreateSecretVersionResult.NotFound($"Secret '{command.Name}' does not exist.");
    }
}

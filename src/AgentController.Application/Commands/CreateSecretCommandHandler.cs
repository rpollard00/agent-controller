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

        if (string.IsNullOrEmpty(command.Value))
        {
            return CreateSecretResult.ValidationFailed(
                new Dictionary<string, string[]>
                {
                    ["value"] = ["Secret value is required."],
                }
            );
        }

        // For now, all secrets created through the command layer default to PAT type.
        // SSH-key secrets will be supported once typed command payloads are introduced.
        var created = await _secretManager.CreateAsync(
            command.Name,
            new PersonalAccessTokenPayload { Value = command.Value },
            cancellationToken
        );

        return created
            ? CreateSecretResult.Succeeded(command.Name)
            : CreateSecretResult.Conflict($"Secret '{command.Name}' already exists.");
    }
}

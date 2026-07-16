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

        if (string.IsNullOrEmpty(command.Value))
        {
            return CreateSecretVersionResult.ValidationFailed(
                new Dictionary<string, string[]>
                {
                    ["value"] = ["Secret value is required."],
                }
            );
        }

        var version = await _secretManager.CreateVersionAsync(
            command.Name,
            command.Value,
            cancellationToken
        );

        return version.HasValue
            ? CreateSecretVersionResult.Succeeded(command.Name, version.Value)
            : CreateSecretVersionResult.NotFound($"Secret '{command.Name}' does not exist.");
    }
}

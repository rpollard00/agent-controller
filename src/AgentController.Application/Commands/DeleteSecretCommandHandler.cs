using AgentController.Application.Abstractions;
using AgentController.Application.Results;
using AgentController.Domain;
using AgentController.Domain.Secrets;

namespace AgentController.Application.Commands;

/// <summary>
/// Validates and deletes an unreferenced named secret (and all of its versions).
/// A secret that is referenced by any connection PAT reference or repository
/// profile is in use and cannot be deleted.
/// </summary>
public sealed class DeleteSecretCommandHandler(
    ISecretManager secretManager,
    IConnectionStore connectionStore,
    IRepositoryStore repositoryStore
) : ICommandHandler<DeleteSecretCommand, DeleteSecretResult>
{
    private readonly ISecretManager _secretManager = secretManager;
    private readonly IConnectionStore _connectionStore = connectionStore;
    private readonly IRepositoryStore _repositoryStore = repositoryStore;

    public async Task<DeleteSecretResult> HandleAsync(
        DeleteSecretCommand command,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(command.Name))
        {
            return DeleteSecretResult.ValidationFailed(
                new Dictionary<string, string[]>
                {
                    ["name"] = ["Secret name is required."],
                }
            );
        }

        if (command.Name.Length > 256)
        {
            return DeleteSecretResult.ValidationFailed(
                new Dictionary<string, string[]>
                {
                    ["name"] = ["Secret name must be 256 characters or fewer."],
                }
            );
        }

        var secrets = await _secretManager.ListAsync(cancellationToken);
        var exists = secrets.Any(secret =>
            string.Equals(secret.Name, command.Name, StringComparison.Ordinal)
        );
        if (!exists)
        {
            return DeleteSecretResult.NotFound($"Secret '{command.Name}' was not found.");
        }

        var connections = await _connectionStore.ListAsync(cancellationToken);
        var referencingConnectionKeys = connections
            .Where(profile =>
                profile.ProviderSettings is AzureDevOpsConnectionSettings ado
                && ado.PersonalAccessTokenReference.IsSpecified
                && string.Equals(
                    ado.PersonalAccessTokenReference.Name,
                    command.Name,
                    StringComparison.Ordinal
                )
            )
            .Select(profile => $"connection '{profile.Key}'");

        var repositories = await _repositoryStore.ListAsync(cancellationToken);
        var referencingRepositoryKeys = repositories
            .Where(profile =>
                string.Equals(
                    profile.PersonalAccessTokenSecretName,
                    command.Name,
                    StringComparison.Ordinal
                )
            )
            .Select(profile => $"repository '{profile.Key}'");

        var references = referencingConnectionKeys
            .Concat(referencingRepositoryKeys)
            .ToList();

        if (references.Count > 0)
        {
            return DeleteSecretResult.Conflict(
                $"Secret '{command.Name}' is referenced by {string.Join(", ", references)}."
            );
        }

        var deleted = await _secretManager.DeleteAsync(command.Name, cancellationToken);
        return deleted
            ? DeleteSecretResult.Succeeded()
            : DeleteSecretResult.NotFound($"Secret '{command.Name}' was not found.");
    }
}

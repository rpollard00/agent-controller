using AgentController.Application.Abstractions;
using AgentController.Domain.Secrets;

namespace AgentController.Application.Queries;

/// <summary>Lists all named secrets via ISecretManager.</summary>
public sealed class ListSecretsQueryHandler(ISecretManager secretManager)
    : IQueryHandler<ListSecretsQuery, IReadOnlyList<SecretInfo>>
{
    private readonly ISecretManager _secretManager = secretManager;

    public async Task<IReadOnlyList<SecretInfo>> ExecuteAsync(
        ListSecretsQuery query,
        CancellationToken cancellationToken
    )
    {
        return await _secretManager.ListAsync(cancellationToken);
    }
}

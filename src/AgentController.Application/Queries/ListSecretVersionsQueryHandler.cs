using AgentController.Application.Abstractions;
using AgentController.Domain.Secrets;

namespace AgentController.Application.Queries;

/// <summary>Lists all versions of a named secret via ISecretManager.</summary>
public sealed class ListSecretVersionsQueryHandler(ISecretManager secretManager)
    : IQueryHandler<ListSecretVersionsQuery, IReadOnlyList<SecretVersionInfo>?>
{
    private readonly ISecretManager _secretManager = secretManager;

    public async Task<IReadOnlyList<SecretVersionInfo>?> ExecuteAsync(
        ListSecretVersionsQuery query,
        CancellationToken cancellationToken
    )
    {
        return await _secretManager.ListVersionsAsync(query.Name, cancellationToken);
    }
}

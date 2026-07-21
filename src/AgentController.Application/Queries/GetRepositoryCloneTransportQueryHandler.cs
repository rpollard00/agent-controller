using AgentController.Application.Abstractions;
using AgentController.Application.Results;
using AgentController.Domain;

namespace AgentController.Application.Queries;

/// <summary>
/// Reads a managed repository and its host connection, then applies the shared domain
/// transport-resolution policy.
/// </summary>
public sealed class GetRepositoryCloneTransportQueryHandler(
    IRepositoryStore repositoryStore,
    IConnectionStore connectionStore
) : IQueryHandler<GetRepositoryCloneTransportQuery, RepositoryCloneTransportQueryResult>
{
    private readonly IRepositoryStore _repositoryStore = repositoryStore;
    private readonly IConnectionStore _connectionStore = connectionStore;

    public async Task<RepositoryCloneTransportQueryResult> ExecuteAsync(
        GetRepositoryCloneTransportQuery query,
        CancellationToken cancellationToken
    )
    {
        var key = RepositoryProfileValidation.ValidateAndNormalizeKey(query.Key);
        if (!key.IsValid)
        {
            return RepositoryCloneTransportQueryResult.ValidationFailed(key.Errors);
        }

        var repository = await _repositoryStore.GetByKeyAsync(key.Key, cancellationToken);
        if (repository is null)
        {
            return RepositoryCloneTransportQueryResult.NotFound(
                $"Repository '{key.Key}' was not found."
            );
        }

        ConnectionProfile? connection = null;
        if (!string.IsNullOrWhiteSpace(repository.RepositoryHostConnectionKey))
        {
            connection = await _connectionStore.GetByKeyAsync(
                repository.RepositoryHostConnectionKey,
                cancellationToken
            );
        }

        var resolution = RepositoryCloneTransportResolver.Resolve(repository, connection);
        return RepositoryCloneTransportQueryResult.Succeeded(resolution);
    }
}

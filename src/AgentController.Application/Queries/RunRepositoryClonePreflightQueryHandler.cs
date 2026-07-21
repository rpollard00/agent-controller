using AgentController.Application.Abstractions;
using AgentController.Application.Results;
using AgentController.Domain;

namespace AgentController.Application.Queries;

/// <summary>
/// Loads the same repository and connection context used by clone execution, then delegates
/// the non-cloning remote probe to the active source-control provider.
/// </summary>
public sealed class RunRepositoryClonePreflightQueryHandler(
    IRepositoryStore repositoryStore,
    IConnectionStore connectionStore,
    ISourceControlProvider sourceControlProvider
) : IQueryHandler<RunRepositoryClonePreflightQuery, RepositoryClonePreflightQueryResult>
{
    private readonly IRepositoryStore _repositoryStore = repositoryStore;
    private readonly IConnectionStore _connectionStore = connectionStore;
    private readonly ISourceControlProvider _sourceControlProvider = sourceControlProvider;

    public async Task<RepositoryClonePreflightQueryResult> ExecuteAsync(
        RunRepositoryClonePreflightQuery query,
        CancellationToken cancellationToken
    )
    {
        var key = RepositoryProfileValidation.ValidateAndNormalizeKey(query.Key);
        if (!key.IsValid)
        {
            return RepositoryClonePreflightQueryResult.ValidationFailed(key.Errors);
        }

        var repository = await _repositoryStore.GetByKeyAsync(key.Key, cancellationToken);
        if (repository is null)
        {
            return RepositoryClonePreflightQueryResult.NotFound(
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

        var preflight = await _sourceControlProvider.CheckClonePreflightAsync(
            new RepositorySpec
            {
                RepoKey = repository.Key,
                CloneUrl = repository.CloneUrl,
                DefaultBranch = repository.DefaultBranch,
                Transport = repository.Transport,
                Profile = repository,
                RepositoryConnection = connection,
            },
            cancellationToken
        );

        return RepositoryClonePreflightQueryResult.Succeeded(preflight);
    }
}

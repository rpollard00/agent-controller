using AgentController.Application.Abstractions;
using AgentController.Application.Results;

namespace AgentController.Application.Queries;

/// <summary>Validates and reads a managed repository profile by key.</summary>
public sealed class GetRepositoryByKeyQueryHandler(IRepositoryStore repositoryStore)
    : IQueryHandler<GetRepositoryByKeyQuery, RepositoryOperationResult>
{
    private readonly IRepositoryStore _repositoryStore = repositoryStore;

    public async Task<RepositoryOperationResult> ExecuteAsync(
        GetRepositoryByKeyQuery query,
        CancellationToken cancellationToken
    )
    {
        var key = RepositoryProfileValidation.ValidateAndNormalizeKey(query.Key);
        if (!key.IsValid)
        {
            return RepositoryOperationResult.ValidationFailed(key.Errors);
        }

        var repository = await _repositoryStore.GetByKeyAsync(key.Key, cancellationToken);
        return repository is null
            ? RepositoryOperationResult.NotFound($"Repository '{key.Key}' was not found.")
            : RepositoryOperationResult.Succeeded(repository);
    }
}

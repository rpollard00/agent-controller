using AgentController.Application.Abstractions;
using AgentController.Application.Results;

namespace AgentController.Application.Queries;

/// <summary>Validates and reads a managed repository host connection by key.</summary>
public sealed class GetRepositoryHostConnectionByKeyQueryHandler(
    IRepositoryHostConnectionStore connectionStore
) : IQueryHandler<GetRepositoryHostConnectionByKeyQuery, RepositoryHostConnectionOperationResult>
{
    private readonly IRepositoryHostConnectionStore _connectionStore = connectionStore;

    public async Task<RepositoryHostConnectionOperationResult> ExecuteAsync(
        GetRepositoryHostConnectionByKeyQuery query,
        CancellationToken cancellationToken
    )
    {
        var key = RepositoryHostConnectionProfileValidation.ValidateAndNormalizeKey(query.Key);
        if (!key.IsValid)
        {
            return RepositoryHostConnectionOperationResult.ValidationFailed(key.Errors);
        }

        var connection = await _connectionStore.GetByKeyAsync(key.Key, cancellationToken);
        return connection is null
            ? RepositoryHostConnectionOperationResult.NotFound(
                $"Repository host connection '{key.Key}' was not found."
            )
            : RepositoryHostConnectionOperationResult.Succeeded(connection);
    }
}

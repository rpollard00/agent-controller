using AgentController.Application.Abstractions;
using AgentController.Application.Results;

namespace AgentController.Application.Queries;

/// <summary>Validates and reads a managed connection by key.</summary>
public sealed class GetConnectionByKeyQueryHandler(
    IConnectionStore connectionStore
) : IQueryHandler<GetConnectionByKeyQuery, ConnectionOperationResult>
{
    private readonly IConnectionStore _connectionStore = connectionStore;

    public async Task<ConnectionOperationResult> ExecuteAsync(
        GetConnectionByKeyQuery query,
        CancellationToken cancellationToken
    )
    {
        var key = ConnectionProfileValidation.ValidateAndNormalizeKey(query.Key);
        if (!key.IsValid)
        {
            return ConnectionOperationResult.ValidationFailed(key.Errors);
        }

        var connection = await _connectionStore.GetByKeyAsync(key.Key, cancellationToken);
        return connection is null
            ? ConnectionOperationResult.NotFound(
                $"Connection '{key.Key}' was not found."
            )
            : ConnectionOperationResult.Succeeded(connection);
    }
}

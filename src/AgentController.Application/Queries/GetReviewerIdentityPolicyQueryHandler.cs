using AgentController.Application.Abstractions;
using AgentController.Application.Results;

namespace AgentController.Application.Queries;

/// <summary>
/// Resolves the current repository-host connection and projects its provider's
/// reviewer identity policy into credential-free metadata.
/// </summary>
public sealed class GetReviewerIdentityPolicyQueryHandler(
    IConnectionStore connectionStore,
    IReviewerIdentityPolicyResolver policyResolver
) : IQueryHandler<GetReviewerIdentityPolicyQuery, ReviewerIdentityPolicyQueryResult>
{
    private readonly IConnectionStore _connectionStore = connectionStore;
    private readonly IReviewerIdentityPolicyResolver _policyResolver = policyResolver;

    public async Task<ReviewerIdentityPolicyQueryResult> ExecuteAsync(
        GetReviewerIdentityPolicyQuery query,
        CancellationToken cancellationToken
    )
    {
        var key = ConnectionProfileValidation.ValidateAndNormalizeKey(query.ConnectionKey);
        if (!key.IsValid)
        {
            return ReviewerIdentityPolicyQueryResult.ValidationFailed(key.Errors);
        }

        var connection = await _connectionStore.GetByKeyAsync(key.Key, cancellationToken);
        if (connection is null)
        {
            return ReviewerIdentityPolicyQueryResult.NotFound(
                $"Connection '{key.Key}' was not found."
            );
        }

        var policy = _policyResolver.Resolve(connection.Provider);
        return ReviewerIdentityPolicyQueryResult.Succeeded(
            ReviewerIdentityPolicyMetadata.FromPolicy(connection.Provider, policy)
        );
    }
}

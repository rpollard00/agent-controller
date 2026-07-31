using AgentController.Application.Abstractions;

namespace AgentController.Application.Results;

/// <summary>
/// Operator-safe reviewer identity metadata for a managed repository-host connection.
/// This projection deliberately contains no provider settings or credentials.
/// </summary>
public sealed record ReviewerIdentityPolicyMetadata
{
    /// <summary>Provider discriminator used to resolve the policy.</summary>
    public string Provider { get; init; } = string.Empty;

    /// <summary>Whether a reviewer identity policy is registered for the provider.</summary>
    public bool IsSupported { get; init; }

    /// <summary>
    /// Identity kinds accepted by the provider. The collection is empty for an unsupported
    /// provider.
    /// </summary>
    public IReadOnlyList<ReviewerIdentityKindDefinition> SupportedIdentityKinds { get; init; } = [];

    /// <summary>Creates a safe metadata projection from a resolved provider policy.</summary>
    public static ReviewerIdentityPolicyMetadata FromPolicy(
        string provider,
        IReviewerIdentityPolicy? policy
    ) => new()
    {
        Provider = provider,
        IsSupported = policy is not null,
        SupportedIdentityKinds = policy?.SupportedIdentityKinds ?? [],
    };
}

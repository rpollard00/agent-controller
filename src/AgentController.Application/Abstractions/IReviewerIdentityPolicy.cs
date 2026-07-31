using AgentController.Domain;

namespace AgentController.Application.Abstractions;

/// <summary>
/// Describes the validation and matching rules for reviewer identities emitted by
/// a repository-host provider.
/// </summary>
public interface IReviewerIdentityPolicy
{
    /// <summary>Provider discriminator handled by this policy.</summary>
    string Provider { get; }

    /// <summary>Identity kinds supported by the provider.</summary>
    IReadOnlyList<ReviewerIdentityKindDefinition> SupportedIdentityKinds { get; }

    /// <summary>Short alias for <see cref="SupportedIdentityKinds"/>.</summary>
    IReadOnlyList<ReviewerIdentityKindDefinition> SupportedKinds => SupportedIdentityKinds;

    /// <summary>
    /// Normalizes and validates one configured or observed reviewer identity.
    /// </summary>
    ReviewerIdentityValidationResult ValidateAndNormalize(ReviewerIdentity identity);

    /// <summary>Returns the normalized identity, or <see langword="null"/> when invalid.</summary>
    ReviewerIdentity? Normalize(ReviewerIdentity identity) =>
        ValidateAndNormalize(identity).NormalizedIdentity;

    /// <summary>Returns validation errors for one identity.</summary>
    IReadOnlyList<string> Validate(ReviewerIdentity identity) =>
        ValidateAndNormalize(identity).Errors;

    /// <summary>
    /// Determines whether two normalized identities represent the same reviewer.
    /// </summary>
    bool Matches(ReviewerIdentity configured, ReviewerIdentity author);

    /// <summary>Short alias for <see cref="Matches"/>.</summary>
    bool IsMatch(ReviewerIdentity configured, ReviewerIdentity author) =>
        Matches(configured, author);
}

/// <summary>Metadata and validation category for one provider identity kind.</summary>
public sealed record ReviewerIdentityKindDefinition
{
    /// <summary>Provider-neutral kind value persisted with an identity.</summary>
    public string Kind { get; init; } = string.Empty;

    /// <summary>Human-readable label for the kind.</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>Optional operator guidance for entering a value.</summary>
    public string? Hint { get; init; }

    /// <summary>Optional input placeholder for the value.</summary>
    public string? Placeholder { get; init; }

    /// <summary>Validation category used by clients to tailor input controls.</summary>
    public ReviewerIdentityValidationCategory ValidationCategory { get; init; }

    /// <summary>Alias for clients that refer to the persisted value as identity kind.</summary>
    public string IdentityKind => Kind;
}

/// <summary>Broad validation category for a provider reviewer identity kind.</summary>
public enum ReviewerIdentityValidationCategory
{
    /// <summary>An email or email-like provider login.</summary>
    Email,

    /// <summary>A canonical GUID.</summary>
#pragma warning disable CA1720 // The validation category intentionally names the GUID format.
    Guid,
#pragma warning restore CA1720

    /// <summary>Alias for <see cref="Guid"/>.</summary>
    GuidValue = Guid,

    /// <summary>An opaque provider-issued value.</summary>
    Opaque,
}

/// <summary>Result of normalizing and validating one reviewer identity.</summary>
public sealed record ReviewerIdentityValidationResult
{
    /// <summary>The normalized identity when validation succeeds.</summary>
    public ReviewerIdentity? NormalizedIdentity { get; init; }

    /// <summary>Validation errors when the identity is not accepted.</summary>
    public IReadOnlyList<string> Errors { get; init; } = [];

    /// <summary>Whether a normalized identity was produced without errors.</summary>
    public bool IsValid => NormalizedIdentity is not null && Errors.Count == 0;

    /// <summary>Convenience alias for callers that use identity rather than normalization terminology.</summary>
    public ReviewerIdentity? Identity => NormalizedIdentity;
}

/// <summary>Resolves reviewer identity policies by repository-host provider.</summary>
public interface IReviewerIdentityPolicyResolver
{
    /// <summary>Returns the policy for a provider, or <see langword="null"/> when unsupported.</summary>
    IReviewerIdentityPolicy? Resolve(string? provider);
}

/// <summary>
/// Default provider-keyed policy resolver. Multiple providers can be registered
/// independently through <see cref="IReviewerIdentityPolicy"/> implementations.
/// </summary>
public sealed class ReviewerIdentityPolicyResolver : IReviewerIdentityPolicyResolver
{
    private readonly Dictionary<string, IReviewerIdentityPolicy> _policies;

    /// <summary>Creates a resolver from the registered provider policies.</summary>
    public ReviewerIdentityPolicyResolver(IEnumerable<IReviewerIdentityPolicy> policies)
    {
        var resolved = new Dictionary<string, IReviewerIdentityPolicy>(
            StringComparer.OrdinalIgnoreCase
        );
        foreach (var policy in policies)
        {
            if (!string.IsNullOrWhiteSpace(policy.Provider))
            {
                // Last registration wins, matching the provider registration behavior
                // used by the other extensible provider ports.
                resolved[policy.Provider.Trim()] = policy;
            }
        }

        _policies = resolved;
    }

    /// <inheritdoc />
    public IReviewerIdentityPolicy? Resolve(string? provider)
    {
        var normalizedProvider = provider?.Trim();
        return normalizedProvider is not null
            && _policies.TryGetValue(normalizedProvider, out var policy)
            ? policy
            : null;
    }
}

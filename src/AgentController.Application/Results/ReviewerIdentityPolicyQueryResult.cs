namespace AgentController.Application.Results;

/// <summary>Identifies the outcome of a reviewer identity policy lookup.</summary>
public enum ReviewerIdentityPolicyQueryStatus
{
    /// <summary>The lookup completed successfully, including for unsupported providers.</summary>
    Succeeded,

    /// <summary>The connection key was invalid.</summary>
    ValidationFailed,

    /// <summary>The requested connection does not exist.</summary>
    NotFound,
}

/// <summary>Application-layer result for a reviewer identity policy lookup.</summary>
public sealed record ReviewerIdentityPolicyQueryResult
{
    private static readonly IReadOnlyDictionary<string, string[]> NoValidationErrors =
        new Dictionary<string, string[]>();

    private ReviewerIdentityPolicyQueryResult(
        ReviewerIdentityPolicyQueryStatus status,
        ReviewerIdentityPolicyMetadata? metadata = null,
        IReadOnlyDictionary<string, string[]>? validationErrors = null,
        string? detail = null
    )
    {
        Status = status;
        Metadata = metadata;
        ValidationErrors = validationErrors ?? NoValidationErrors;
        Detail = detail;
    }

    /// <summary>The lookup outcome.</summary>
    public ReviewerIdentityPolicyQueryStatus Status { get; }

    /// <summary>Safe provider metadata for successful lookups.</summary>
    public ReviewerIdentityPolicyMetadata? Metadata { get; }

    /// <summary>Field-keyed validation errors for an invalid connection key.</summary>
    public IReadOnlyDictionary<string, string[]> ValidationErrors { get; }

    /// <summary>Safe detail for a not-found outcome.</summary>
    public string? Detail { get; }

    public static ReviewerIdentityPolicyQueryResult Succeeded(
        ReviewerIdentityPolicyMetadata metadata
    ) => new(ReviewerIdentityPolicyQueryStatus.Succeeded, metadata);

    public static ReviewerIdentityPolicyQueryResult ValidationFailed(
        IReadOnlyDictionary<string, string[]> errors
    ) => new(ReviewerIdentityPolicyQueryStatus.ValidationFailed, validationErrors: errors);

    public static ReviewerIdentityPolicyQueryResult NotFound(string detail) =>
        new(ReviewerIdentityPolicyQueryStatus.NotFound, detail: detail);
}

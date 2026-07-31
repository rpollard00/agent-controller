namespace AgentController.Application.Queries;

/// <summary>Gets reviewer identity metadata for a managed repository-host connection.</summary>
public sealed record GetReviewerIdentityPolicyQuery(
    /// <summary>Key of the selected repository-host connection.</summary>
    string ConnectionKey
);

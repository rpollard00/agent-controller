using AgentController.Domain.Secrets;

namespace AgentController.Application.Queries;

/// <summary>Lists all versions of a named secret (metadata only, no values).</summary>
public sealed record ListSecretVersionsQuery(
    /// <summary>The secret name.</summary>
    string Name
);

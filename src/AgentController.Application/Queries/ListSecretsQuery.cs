using AgentController.Domain.Secrets;

namespace AgentController.Application.Queries;

/// <summary>Lists all named secrets (metadata only, no values).</summary>
public sealed record ListSecretsQuery;

namespace AgentController.Application.Queries;

/// <summary>Runs a non-cloning credential-aware remote probe for a managed repository.</summary>
public sealed record RunRepositoryClonePreflightQuery(string Key);

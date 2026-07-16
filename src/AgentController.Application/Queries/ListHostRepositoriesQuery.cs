namespace AgentController.Application.Queries;

/// <summary>
/// Lists repositories available from a managed repository host connection identified by <paramref name="ConnectionKey"/>.
/// </summary>
public sealed record ListHostRepositoriesQuery(string ConnectionKey);

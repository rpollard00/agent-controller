using AgentController.Application.Abstractions;

namespace AgentController.Application.Queries;

/// <summary>
/// Lists projects available from a managed connection identified by <paramref name="Key"/>.
/// </summary>
public sealed record ListConnectionProjectsQuery(string Key);

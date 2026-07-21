namespace AgentController.Application.Queries;

/// <summary>Requests the effective clone transport for a managed repository.</summary>
public sealed record GetRepositoryCloneTransportQuery(string Key);

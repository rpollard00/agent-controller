namespace AgentController.Application.Queries;

/// <summary>
/// Verifies connectivity for a managed connection identified by <paramref name="Key"/>.
/// </summary>
public sealed record VerifyConnectionQuery(string Key);

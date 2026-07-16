namespace AgentController.Application.Queries;

/// <summary>
/// Verifies connectivity for a managed repository host connection identified by <paramref name="Key"/>.
/// </summary>
public sealed record VerifyRepositoryHostConnectivityQuery(string Key);

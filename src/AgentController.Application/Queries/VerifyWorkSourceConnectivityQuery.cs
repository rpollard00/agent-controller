namespace AgentController.Application.Queries;

/// <summary>
/// Verifies connectivity for a managed work-source environment identified by <paramref name="Key"/>.
/// </summary>
public sealed record VerifyWorkSourceConnectivityQuery(string Key);

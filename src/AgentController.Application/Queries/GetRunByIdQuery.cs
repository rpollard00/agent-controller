namespace AgentController.Application.Queries;

/// <summary>
/// Query to retrieve full details for a single agent run by its controller-assigned identifier.
/// </summary>
public sealed record GetRunByIdQuery(string RunId);

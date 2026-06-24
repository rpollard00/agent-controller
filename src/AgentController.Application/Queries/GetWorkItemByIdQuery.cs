namespace AgentController.Application.Queries;

/// <summary>
/// Query to retrieve a single work item by its controller-assigned identifier.
/// </summary>
public sealed record GetWorkItemByIdQuery(string Id);

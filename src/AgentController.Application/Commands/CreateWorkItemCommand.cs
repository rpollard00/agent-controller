using AgentController.Domain;

namespace AgentController.Application.Commands;

/// <summary>
/// Command to create a new local work item.
/// Wraps the domain <see cref="CreateWorkItemRequest"/> to keep the command
/// layer decoupled from HTTP request shapes.
/// </summary>
public sealed record CreateWorkItemCommand(
    CreateWorkItemRequest Request
);

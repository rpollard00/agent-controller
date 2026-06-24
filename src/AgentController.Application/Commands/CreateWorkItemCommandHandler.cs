using AgentController.Application.Abstractions;
using AgentController.Domain;

namespace AgentController.Application.Commands;

/// <summary>
/// Handles <see cref="CreateWorkItemCommand"/> by delegating to <see cref="IWorkItemStore.CreateAsync"/>.
/// </summary>
public sealed class CreateWorkItemCommandHandler(
    IWorkItemStore workItemStore
) : ICommandHandler<CreateWorkItemCommand, WorkCandidate>
{
    private readonly IWorkItemStore _workItemStore = workItemStore;

    public async Task<WorkCandidate> HandleAsync(
        CreateWorkItemCommand command,
        CancellationToken cancellationToken
    )
    {
        return await _workItemStore.CreateAsync(command.Request, cancellationToken);
    }
}

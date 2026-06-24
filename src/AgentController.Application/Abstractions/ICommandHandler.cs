namespace AgentController.Application.Abstractions;

public interface ICommandHandler<in TCommand, TResult> where TCommand : notnull
{
    Task<TResult> HandleAsync(TCommand command, CancellationToken cancellationToken);
}

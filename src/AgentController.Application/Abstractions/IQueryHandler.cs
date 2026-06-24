namespace AgentController.Application.Abstractions;

public interface IQueryHandler<in TQuery, TResult> where TQuery : notnull
{
    Task<TResult> ExecuteAsync(TQuery query, CancellationToken cancellationToken);
}

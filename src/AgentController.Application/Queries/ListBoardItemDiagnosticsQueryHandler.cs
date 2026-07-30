using AgentController.Application.Abstractions;

namespace AgentController.Application.Queries;

/// <summary>Evaluates pickup policy for a discovered page without changing provider data.</summary>
public sealed class ListBoardItemDiagnosticsQueryHandler(
    IManagedBoardItemDiscovery discovery,
    IManagedProfileResolver profileResolver,
    IRepositoryStore repositoryStore,
    IWorkItemStore workItemStore,
    IReworkCycleStore reworkCycleStore)
    : IQueryHandler<ListBoardItemDiagnosticsQuery, BoardItemDiagnosticsPage>
{
    public async Task<BoardItemDiagnosticsPage> ExecuteAsync(
        ListBoardItemDiagnosticsQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var discovered = await discovery.ListAsync(
            new ManagedBoardItemDiscoveryQuery
            {
                WorkSourceEnvironmentKey = query.WorkSourceEnvironmentKey,
                IncludeTerminal = query.IncludeTerminal,
                Page = query.Page,
                PageSize = query.PageSize,
            },
            cancellationToken);
        var evaluator = await BoardItemPickupEvaluator.CreateAsync(
            profileResolver,
            repositoryStore,
            workItemStore,
            reworkCycleStore,
            cancellationToken);
        var summaries = new List<BoardItemDiagnosticSummary>(discovered.Items.Count);

        foreach (var snapshot in discovered.Items)
        {
            var evaluated = await evaluator.EvaluateAsync(snapshot, cancellationToken);
            summaries.Add(ToSummary(evaluated));
        }

        return new BoardItemDiagnosticsPage
        {
            Items = summaries,
            Failures = discovered.Failures,
            Page = discovered.Page,
            PageSize = discovered.PageSize,
            Total = discovered.Total,
        };
    }

    private static BoardItemDiagnosticSummary ToSummary(EvaluatedBoardItem evaluated)
    {
        var snapshot = evaluated.Snapshot;
        return new BoardItemDiagnosticSummary
        {
            Id = snapshot.Item.ExternalId,
            Title = snapshot.Item.Title,
            Url = snapshot.Item.ExternalUrl,
            Project = snapshot.Project,
            WorkSourceEnvironmentKey = snapshot.WorkSourceEnvironmentKey,
            RepositoryKey = evaluated.RepositoryKey,
            State = snapshot.Item.Status,
            Match = evaluated.Match,
        };
    }
}

using AgentController.Application.Abstractions;

namespace AgentController.Application.Queries;

/// <summary>Lazily discovers and evaluates one board item.</summary>
public sealed class GetBoardItemDiagnosticsQueryHandler(
    IManagedBoardItemDiscovery discovery,
    IManagedProfileResolver profileResolver,
    IRepositoryStore repositoryStore,
    IWorkItemStore workItemStore,
    IReworkCycleStore reworkCycleStore)
    : IQueryHandler<GetBoardItemDiagnosticsQuery, BoardItemDiagnosticDetail?>
{
    public async Task<BoardItemDiagnosticDetail?> ExecuteAsync(
        GetBoardItemDiagnosticsQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentException.ThrowIfNullOrWhiteSpace(query.WorkSourceEnvironmentKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(query.ItemId);

        var snapshot = await discovery.GetAsync(
            new ManagedBoardItemDiscoveryItemQuery
            {
                WorkSourceEnvironmentKey = query.WorkSourceEnvironmentKey,
                ItemId = query.ItemId,
            },
            cancellationToken);
        if (snapshot is null)
        {
            return null;
        }

        var evaluator = await BoardItemPickupEvaluator.CreateAsync(
            profileResolver,
            repositoryStore,
            workItemStore,
            reworkCycleStore,
            cancellationToken);
        var evaluated = await evaluator.EvaluateAsync(snapshot, cancellationToken);
        var item = snapshot.Item;

        return new BoardItemDiagnosticDetail
        {
            Id = item.ExternalId,
            Title = item.Title,
            Url = item.ExternalUrl,
            Project = snapshot.Project,
            WorkSourceEnvironmentKey = snapshot.WorkSourceEnvironmentKey,
            RepositoryKey = evaluated.RepositoryKey,
            State = item.Status,
            Tags = evaluated.Tags,
            Match = evaluated.Match,
            Eligible = evaluated.Match == BoardItemMatchResult.Eligible,
            Checks = evaluated.Checks,
            RecognizedRepositoryTags = evaluated.RecognizedRepositoryTags,
            RecognizedReadyTag = evaluated.ReadyTag,
            RecognizedReadyReworkTag = evaluated.ReadyReworkTag,
        };
    }
}

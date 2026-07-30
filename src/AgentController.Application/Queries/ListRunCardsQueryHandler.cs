using AgentController.Application.Abstractions;
using AgentController.Application.Results;
using AgentController.Domain;
using Microsoft.Extensions.Options;

namespace AgentController.Application.Queries;

/// <summary>
/// Builds the runs-dashboard projection from agent runs and tracked rework requests.
/// </summary>
public sealed class ListRunCardsQueryHandler(
    IAgentRunStore runStore,
    IWorkItemStore workItemStore,
    ILifecycleEventStore lifecycleEventStore,
    IRepositoryStore repositoryStore,
    IReworkFeedbackStore reworkFeedbackStore,
    IReworkCycleStore reworkCycleStore,
    IOptions<FeedbackSoakOptionsView> feedbackSoakOptions
) : IQueryHandler<ListRunCardsQuery, IReadOnlyList<RunCardItem>>
{
    private const int ResultLimit = 200;

    private readonly IAgentRunStore _runStore = runStore;
    private readonly IWorkItemStore _workItemStore = workItemStore;
    private readonly ILifecycleEventStore _lifecycleEventStore = lifecycleEventStore;
    private readonly IRepositoryStore _repositoryStore = repositoryStore;
    private readonly IReworkFeedbackStore _reworkFeedbackStore = reworkFeedbackStore;
    private readonly IReworkCycleStore _reworkCycleStore = reworkCycleStore;
    private readonly TimeSpan _feedbackSoakDuration = feedbackSoakOptions.Value.SoakDuration;

    public async Task<IReadOnlyList<RunCardItem>> ExecuteAsync(
        ListRunCardsQuery query,
        CancellationToken cancellationToken
    )
    {
        var runs = await ListAllRunsAsync(cancellationToken);
        var trackedFeedback = await _reworkFeedbackStore.GetTrackedAsync(cancellationToken);
        var pendingCycles = await _reworkCycleStore.ListPendingAsync(cancellationToken);
        var consumedCycles = await _reworkCycleStore.ListConsumedAsync(cancellationToken);
        var cycles = pendingCycles.Concat(consumedCycles).ToList();

        var runsById = new Dictionary<string, AgentRunHandle>(StringComparer.Ordinal);
        foreach (var run in runs)
        {
            runsById[run.RunId] = run;
        }

        var consumedCyclesByRunId = new Dictionary<string, ReworkCycle>(StringComparer.Ordinal);
        foreach (var cycle in consumedCycles)
        {
            if (!string.IsNullOrWhiteSpace(cycle.NewRunId))
            {
                consumedCyclesByRunId[cycle.NewRunId] = cycle;
            }
        }

        var workItemsById = new Dictionary<string, WorkCandidate?>(StringComparer.Ordinal);
        var repositoryUrlsByKey = new Dictionary<string, string?>(StringComparer.Ordinal);
        var cards = new List<RunCardItem>(runs.Count + trackedFeedback.Count);

        foreach (var run in runs)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var enrichment = await ResolveEnrichmentAsync(
                run,
                workItemsById,
                repositoryUrlsByKey,
                cancellationToken
            );
            var events = await _lifecycleEventStore.ListByRunIdAsync(
                run.RunId,
                cancellationToken
            );
            var latestEvent = events.MaxBy(lifecycleEvent => lifecycleEvent.CreatedAt);
            var cycle = await ResolveCycleForRunAsync(
                run,
                runsById,
                consumedCyclesByRunId,
                pendingCycles,
                cancellationToken
            );
            var feedback = FindFeedbackForCycle(cycle, trackedFeedback);

            cards.Add(
                RunCardFactory.CreateRunCard(
                    run,
                    enrichment.WorkItem,
                    enrichment.RepositoryUrl,
                    latestEvent,
                    cycle,
                    feedback
                )
            );
        }

        foreach (var feedback in trackedFeedback)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var cycle = FindCycleForFeedback(feedback, cycles);
            if (!string.IsNullOrWhiteSpace(cycle?.NewRunId)
                && runsById.ContainsKey(cycle.NewRunId))
            {
                // The consuming run card carries the same tracking projection.
                continue;
            }

            var associatedRun = await ResolveAssociatedRunAsync(
                feedback,
                cycle,
                runsById,
                cancellationToken
            );
            var enrichment = await ResolveFeedbackEnrichmentAsync(
                feedback,
                associatedRun,
                workItemsById,
                repositoryUrlsByKey,
                cancellationToken
            );

            cards.Add(
                RunCardFactory.CreateTrackingCard(
                    feedback,
                    cycle,
                    associatedRun,
                    enrichment.WorkItem,
                    enrichment.RepositoryUrl,
                    _feedbackSoakDuration
                )
            );
        }

        return cards
            .OrderByDescending(card => card.LastEventAt ?? card.UpdatedAt)
            .ThenByDescending(card => card.UpdatedAt)
            .ThenBy(card => card.Id, StringComparer.Ordinal)
            .Take(ResultLimit)
            .ToList();
    }

    private async Task<IReadOnlyList<AgentRunHandle>> ListAllRunsAsync(
        CancellationToken cancellationToken
    )
    {
        var runs = new List<AgentRunHandle>();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await _runStore.ListAsync(
                new ListRunsQuery { MaxResults = ResultLimit, Offset = runs.Count },
                cancellationToken
            );
            runs.AddRange(page);

            if (page.Count < ResultLimit)
            {
                return runs;
            }
        }
    }

    internal static string ClassifyCategory(RunLifecycleState status) =>
        RunCardFactory.ClassifyCategory(status);

    private Task<RunEnrichment> ResolveEnrichmentAsync(
        AgentRunHandle run,
        Dictionary<string, WorkCandidate?> workItemsById,
        Dictionary<string, string?> repositoryUrlsByKey,
        CancellationToken cancellationToken
    ) =>
        ResolveWorkItemEnrichmentAsync(
            run.WorkItemId,
            workItemsById,
            repositoryUrlsByKey,
            cancellationToken
        );

    private async Task<RunEnrichment> ResolveFeedbackEnrichmentAsync(
        ReworkFeedback feedback,
        AgentRunHandle? associatedRun,
        Dictionary<string, WorkCandidate?> workItemsById,
        Dictionary<string, string?> repositoryUrlsByKey,
        CancellationToken cancellationToken
    )
    {
        var workItemId = feedback.AssistanceStoryWorkItemId ?? associatedRun?.WorkItemId;
        var enrichment = await ResolveWorkItemEnrichmentAsync(
            workItemId,
            workItemsById,
            repositoryUrlsByKey,
            cancellationToken
        );
        if (enrichment.RepositoryUrl is not null
            || string.IsNullOrWhiteSpace(feedback.PullRequest.RepositoryKey))
        {
            return enrichment;
        }

        var repositoryUrl = await ResolveRepositoryUrlAsync(
            feedback.PullRequest.RepositoryKey,
            repositoryUrlsByKey,
            cancellationToken
        );
        return new RunEnrichment(enrichment.WorkItem, repositoryUrl);
    }

    private async Task<RunEnrichment> ResolveWorkItemEnrichmentAsync(
        string? workItemId,
        Dictionary<string, WorkCandidate?> workItemsById,
        Dictionary<string, string?> repositoryUrlsByKey,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(workItemId))
        {
            return RunEnrichment.Empty;
        }

        if (!workItemsById.TryGetValue(workItemId, out var workItem))
        {
            workItem = await _workItemStore.GetByIdAsync(workItemId, cancellationToken);
            workItemsById[workItemId] = workItem;
        }

        if (workItem is null || string.IsNullOrWhiteSpace(workItem.RepoKey))
        {
            return new RunEnrichment(workItem, null);
        }

        var repositoryUrl = await ResolveRepositoryUrlAsync(
            workItem.RepoKey,
            repositoryUrlsByKey,
            cancellationToken
        );
        return new RunEnrichment(workItem, repositoryUrl);
    }

    private async Task<string?> ResolveRepositoryUrlAsync(
        string repositoryKey,
        Dictionary<string, string?> repositoryUrlsByKey,
        CancellationToken cancellationToken
    )
    {
        if (repositoryUrlsByKey.TryGetValue(repositoryKey, out var repositoryUrl))
        {
            return repositoryUrl;
        }

        var repository = await _repositoryStore.GetByKeyAsync(
            repositoryKey,
            cancellationToken
        );
        repositoryUrl = ResolveRepositoryUrl(repository);
        repositoryUrlsByKey[repositoryKey] = repositoryUrl;
        return repositoryUrl;
    }

    private async Task<ReworkCycle?> ResolveCycleForRunAsync(
        AgentRunHandle run,
        Dictionary<string, AgentRunHandle> runsById,
        Dictionary<string, ReworkCycle> consumedCyclesByRunId,
        IReadOnlyList<ReworkCycle> pendingCycles,
        CancellationToken cancellationToken
    )
    {
        var visitedRunIds = new HashSet<string>(StringComparer.Ordinal);
        AgentRunHandle? lineageRun = run;
        while (lineageRun is not null && visitedRunIds.Add(lineageRun.RunId))
        {
            if (consumedCyclesByRunId.TryGetValue(lineageRun.RunId, out var consumedCycle))
            {
                return consumedCycle;
            }

            if (string.IsNullOrWhiteSpace(lineageRun.PreviousRunId))
            {
                break;
            }

            if (!runsById.TryGetValue(lineageRun.PreviousRunId, out var previousRun))
            {
                previousRun = await _runStore.GetByIdAsync(
                    lineageRun.PreviousRunId,
                    cancellationToken
                );
                if (previousRun is not null)
                {
                    runsById[previousRun.RunId] = previousRun;
                }
            }

            lineageRun = previousRun;
        }

        return string.IsNullOrWhiteSpace(run.WorkItemId)
            ? null
            : pendingCycles
                .Where(cycle => cycle.WorkItemId == run.WorkItemId)
                .OrderBy(cycle => cycle.CycleNumber)
                .FirstOrDefault();
    }

    private async Task<AgentRunHandle?> ResolveAssociatedRunAsync(
        ReworkFeedback feedback,
        ReworkCycle? cycle,
        Dictionary<string, AgentRunHandle> runsById,
        CancellationToken cancellationToken
    )
    {
        var runId = cycle?.NewRunId ?? feedback.OriginatingRunId;
        if (string.IsNullOrWhiteSpace(runId))
        {
            return null;
        }

        if (!runsById.TryGetValue(runId, out var run))
        {
            run = await _runStore.GetByIdAsync(runId, cancellationToken);
            if (run is not null)
            {
                runsById[run.RunId] = run;
            }
        }

        return run;
    }

    private static ReworkFeedback? FindFeedbackForCycle(
        ReworkCycle? cycle,
        IReadOnlyList<ReworkFeedback> feedback
    ) =>
        cycle is null
            ? null
            : feedback.FirstOrDefault(candidate =>
                ReworkTrackingMatcher.IsSameMaterialization(candidate, cycle)
            );

    private static ReworkCycle? FindCycleForFeedback(
        ReworkFeedback feedback,
        IReadOnlyList<ReworkCycle> cycles
    ) =>
        cycles.FirstOrDefault(cycle =>
            ReworkTrackingMatcher.IsSameMaterialization(feedback, cycle)
        );

    private static string? ResolveRepositoryUrl(RepositoryProfile? repository)
    {
        if (!string.IsNullOrWhiteSpace(repository?.WebUrl))
        {
            return repository.WebUrl;
        }

        var cloneUrl = repository?.CloneUrl;
        return cloneUrl?.StartsWith("http://", StringComparison.OrdinalIgnoreCase) == true
            || cloneUrl?.StartsWith("https://", StringComparison.OrdinalIgnoreCase) == true
            ? cloneUrl
            : null;
    }

    private sealed record RunEnrichment(WorkCandidate? WorkItem, string? RepositoryUrl)
    {
        public static RunEnrichment Empty { get; } = new(null, null);
    }
}

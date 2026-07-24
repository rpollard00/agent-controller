using AgentController.Application.Abstractions;
using AgentController.Application.Results;
using AgentController.Domain;

namespace AgentController.Application.Queries;

/// <summary>
/// Builds the runs-dashboard projection from agent runs and active rework-feedback soaks.
/// </summary>
public sealed class ListRunCardsQueryHandler(
    IAgentRunStore runStore,
    IWorkItemStore workItemStore,
    ILifecycleEventStore lifecycleEventStore,
    IRepositoryStore repositoryStore,
    IReworkFeedbackStore reworkFeedbackStore
) : IQueryHandler<ListRunCardsQuery, IReadOnlyList<RunCardItem>>
{
    private const int ResultLimit = 200;
    private const string RunKind = "run";
    private const string ReworkSoakKind = "rework-soak";
    private const string PendingCategory = "pending";

    private readonly IAgentRunStore _runStore = runStore;
    private readonly IWorkItemStore _workItemStore = workItemStore;
    private readonly ILifecycleEventStore _lifecycleEventStore = lifecycleEventStore;
    private readonly IRepositoryStore _repositoryStore = repositoryStore;
    private readonly IReworkFeedbackStore _reworkFeedbackStore = reworkFeedbackStore;

    public async Task<IReadOnlyList<RunCardItem>> ExecuteAsync(
        ListRunCardsQuery query,
        CancellationToken cancellationToken
    )
    {
        var runs = await ListAllRunsAsync(cancellationToken);
        var watchingFeedback = await _reworkFeedbackStore.GetWatchingAsync(cancellationToken);

        var runsById = new Dictionary<string, AgentRunHandle>(StringComparer.Ordinal);
        foreach (var run in runs)
        {
            runsById[run.RunId] = run;
        }

        var workItemsById = new Dictionary<string, WorkCandidate?>(StringComparer.Ordinal);
        var repositoryUrlsByKey = new Dictionary<string, string?>(StringComparer.Ordinal);
        var cards = new List<RunCardItem>(runs.Count + watchingFeedback.Count);

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

            cards.Add(CreateRunCard(run, enrichment, latestEvent));
        }

        foreach (var feedback in watchingFeedback)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!runsById.TryGetValue(feedback.OriginatingRunId, out var originatingRun))
            {
                originatingRun = await _runStore.GetByIdAsync(
                    feedback.OriginatingRunId,
                    cancellationToken
                );
                if (originatingRun is not null)
                {
                    runsById[originatingRun.RunId] = originatingRun;
                }
            }

            var enrichment = originatingRun is null
                ? RunEnrichment.Empty
                : await ResolveEnrichmentAsync(
                    originatingRun,
                    workItemsById,
                    repositoryUrlsByKey,
                    cancellationToken
                );

            cards.Add(CreateReworkSoakCard(feedback, originatingRun, enrichment));
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
        status switch
        {
            RunLifecycleState.AgentStarting
                or RunLifecycleState.AgentRunning
                or RunLifecycleState.AwaitingResult => "executing",

            RunLifecycleState.Queued
                or RunLifecycleState.Claimed
                or RunLifecycleState.EnvironmentProvisioning
                or RunLifecycleState.EnvironmentReady
                or RunLifecycleState.RepositoryCloning
                or RunLifecycleState.RepositoryReady
                or RunLifecycleState.ContextInjected => PendingCategory,

            RunLifecycleState.NeedsHuman
                or RunLifecycleState.Failed
                or RunLifecycleState.Cancelled => "attention",

            RunLifecycleState.ResultReceived
                or RunLifecycleState.PrOpened
                or RunLifecycleState.BranchPushed
                or RunLifecycleState.Completed
                or RunLifecycleState.CleanupPending
                or RunLifecycleState.CleanedUp => "completed",

            _ => throw new ArgumentOutOfRangeException(
                nameof(status),
                status,
                "Unknown run lifecycle state."
            ),
        };

    private async Task<RunEnrichment> ResolveEnrichmentAsync(
        AgentRunHandle run,
        Dictionary<string, WorkCandidate?> workItemsById,
        Dictionary<string, string?> repositoryUrlsByKey,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(run.WorkItemId))
        {
            return RunEnrichment.Empty;
        }

        if (!workItemsById.TryGetValue(run.WorkItemId, out var workItem))
        {
            workItem = await _workItemStore.GetByIdAsync(run.WorkItemId, cancellationToken);
            workItemsById[run.WorkItemId] = workItem;
        }

        if (workItem is null || string.IsNullOrWhiteSpace(workItem.RepoKey))
        {
            return new RunEnrichment(workItem, null);
        }

        if (!repositoryUrlsByKey.TryGetValue(workItem.RepoKey, out var repositoryUrl))
        {
            var repository = await _repositoryStore.GetByKeyAsync(
                workItem.RepoKey,
                cancellationToken
            );
            repositoryUrl = ResolveRepositoryUrl(repository);
            repositoryUrlsByKey[workItem.RepoKey] = repositoryUrl;
        }

        return new RunEnrichment(workItem, repositoryUrl);
    }

    private static RunCardItem CreateRunCard(
        AgentRunHandle run,
        RunEnrichment enrichment,
        LifecycleEvent? latestEvent
    ) =>
        new()
        {
            Id = run.RunId,
            Kind = RunKind,
            Status = run.Status.ToString(),
            Category = ClassifyCategory(run.Status),
            WorkItemTitle = enrichment.WorkItem?.Title,
            WorkItemUrl = enrichment.WorkItem?.ExternalUrl,
            WorkItemSource = enrichment.WorkItem?.Source,
            RepoKey = enrichment.WorkItem?.RepoKey,
            RepositoryUrl = enrichment.RepositoryUrl,
            RuntimeType = run.RuntimeType,
            RunAttempt = run.RunAttempt,
            LastEventType = latestEvent?.EventType,
            LastEventMessage = latestEvent?.Message,
            LastEventAt = latestEvent?.CreatedAt,
            CreatedAt = run.CreatedAt,
            UpdatedAt = run.UpdatedAt,
        };

    private static RunCardItem CreateReworkSoakCard(
        ReworkFeedback feedback,
        AgentRunHandle? originatingRun,
        RunEnrichment enrichment
    )
    {
        var threadLabel = feedback.ThreadCount == 1 ? "thread" : "threads";

        return new RunCardItem
        {
            Id = feedback.Id,
            Kind = ReworkSoakKind,
            Status = "Rework feedback soaking",
            Category = PendingCategory,
            WorkItemTitle = enrichment.WorkItem?.Title,
            WorkItemUrl = enrichment.WorkItem?.ExternalUrl,
            WorkItemSource = enrichment.WorkItem?.Source,
            RepoKey = enrichment.WorkItem?.RepoKey,
            RepositoryUrl = enrichment.RepositoryUrl,
            RuntimeType = originatingRun?.RuntimeType,
            RunAttempt = originatingRun?.RunAttempt ?? 1,
            LastEventType = "rework.feedback.soaking",
            LastEventMessage = $"{feedback.ThreadCount} feedback {threadLabel} awaiting soak",
            LastEventAt = feedback.LastQualifyingCommentAt,
            CreatedAt = feedback.CreatedAt,
            UpdatedAt = feedback.UpdatedAt,
        };
    }

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

using AgentController.Domain;

namespace AgentController.Application.Queries;

internal sealed class BoardItemPickupEvaluator
{
    private readonly IManagedProfileResolver _profileResolver;
    private readonly IWorkItemStore _workItemStore;
    private readonly IReworkCycleStore _reworkCycleStore;
    private readonly IReadOnlyDictionary<string, WorkSourceEnvironmentProfile> _environments;
    private readonly IReadOnlyList<string> _recognizedRepositoryTags;

    private BoardItemPickupEvaluator(
        IManagedProfileResolver profileResolver,
        IWorkItemStore workItemStore,
        IReworkCycleStore reworkCycleStore,
        IReadOnlyDictionary<string, WorkSourceEnvironmentProfile> environments,
        IReadOnlyList<string> recognizedRepositoryTags)
    {
        _profileResolver = profileResolver;
        _workItemStore = workItemStore;
        _reworkCycleStore = reworkCycleStore;
        _environments = environments;
        _recognizedRepositoryTags = recognizedRepositoryTags;
    }

    public static async Task<BoardItemPickupEvaluator> CreateAsync(
        IManagedProfileResolver profileResolver,
        IRepositoryStore repositoryStore,
        IWorkItemStore workItemStore,
        IReworkCycleStore reworkCycleStore,
        CancellationToken cancellationToken)
    {
        var environments = await profileResolver.ListConfiguredWorkSourceEnvironmentsAsync(
            cancellationToken);
        var repositories = await repositoryStore.ListAsync(cancellationToken);

        return new BoardItemPickupEvaluator(
            profileResolver,
            workItemStore,
            reworkCycleStore,
            environments.ToDictionary(
                environment => environment.Profile.Key,
                environment => environment.Profile,
                StringComparer.OrdinalIgnoreCase),
            repositories
                .OrderBy(repository => repository.Key, StringComparer.OrdinalIgnoreCase)
                .Select(repository => $"repo:{repository.Key}")
                .ToArray());
    }

    public async Task<EvaluatedBoardItem> EvaluateAsync(
        ManagedBoardItemSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var item = snapshot.Item;
        var tags = item.Tags.ToArray();
        _environments.TryGetValue(snapshot.WorkSourceEnvironmentKey, out var environment);
        var prefix = BoardItemPickupPolicy.NormalizeTagPrefix(environment?.TagPrefix);
        var readyTag = BoardItemPickupPolicy.ReadyTag(prefix);
        var readyReworkTag = BoardItemPickupPolicy.ReadyReworkTag(prefix);
        var lifecycleTags = BoardItemPickupPolicy.LifecycleTags(prefix);

        var sourceEnabled = environment?.Enabled == true;
        var nonterminal = !BoardItemPickupPolicy.IsTerminal(item.Status);
        var hasReady = BoardItemPickupPolicy.HasTag(tags, readyTag);
        var hasReadyRework = BoardItemPickupPolicy.HasTag(tags, readyReworkTag);
        var hasReadyMarker = hasReady || hasReadyRework;
        var presentLifecycleTags = lifecycleTags
            .Where(tag => BoardItemPickupPolicy.HasTag(tags, tag))
            .ToArray();
        var noLifecycleExclusion = presentLifecycleTags.Length == 0;

        ResolvedControllerProfiles? resolvedRepository = null;
        if (!string.IsNullOrWhiteSpace(item.RepoKey))
        {
            resolvedRepository = await _profileResolver.ResolveForRepositoryAsync(
                item.RepoKey,
                cancellationToken);
        }

        ReworkCycle? pendingCycle = null;
        if (hasReadyRework)
        {
            var persistedItem = await _workItemStore.GetByExternalIdentityAsync(
                item.Source,
                item.ExternalId,
                cancellationToken);
            if (persistedItem is not null)
            {
                pendingCycle = await _reworkCycleStore.GetPendingForWorkItemAsync(
                    persistedItem.Id,
                    cancellationToken);
            }
        }

        var validAssistanceCycle = !hasReadyRework
            || pendingCycle?.RequestMode == ReworkRequestMode.Assistance;
        var checks = new[]
        {
            Check("source-enabled", "Work source enabled", sourceEnabled,
                sourceEnabled ? "The work source is enabled for pickup." : "The work source is disabled or no longer configured."),
            Check("nonterminal-state", "Nonterminal state", nonterminal,
                nonterminal ? "The current state is not terminal." : $"State '{item.Status}' is terminal."),
            Check("ready-marker", "Ready marker", hasReadyMarker,
                hasReadyMarker ? $"Found '{(hasReadyRework ? readyReworkTag : readyTag)}'." : $"Add '{readyTag}' or '{readyReworkTag}'."),
            Check("lifecycle-exclusions", "No lifecycle exclusion", noLifecycleExclusion,
                noLifecycleExclusion ? "No excluding lifecycle tag is present." : $"Excluding tag(s): {string.Join(", ", presentLifecycleTags)}."),
            Check("managed-repository", "Managed repository", resolvedRepository is not null,
                resolvedRepository is not null
                    ? $"Resolved managed repository '{resolvedRepository.Repository.Key}'."
                    : string.IsNullOrWhiteSpace(item.RepoKey)
                        ? "No repo:{key} tag is present."
                        : $"The repo:{item.RepoKey} tag does not resolve to an enabled managed repository and runtime."),
            Check("pending-assistance-cycle", "Pending Assistance cycle", validAssistanceCycle,
                !hasReadyRework
                    ? "Not required for new work."
                    : pendingCycle is null
                        ? "The ready-rework marker requires a pending Assistance cycle."
                        : pendingCycle.RequestMode == ReworkRequestMode.Assistance
                            ? "A pending Assistance cycle is available."
                            : $"The pending cycle is {pendingCycle.RequestMode}, not Assistance."),
        };

        var eligible = checks.All(check => check.Passed);
        var match = eligible
            ? BoardItemMatchResult.Eligible
            : !hasReadyMarker || resolvedRepository is null
                ? BoardItemMatchResult.MissingTags
                : BoardItemMatchResult.Excluded;

        return new EvaluatedBoardItem(
            snapshot,
            resolvedRepository?.Repository.Key,
            match,
            checks,
            _recognizedRepositoryTags,
            readyTag,
            readyReworkTag,
            tags);
    }

    private static BoardItemDiagnosticCheck Check(
        string code,
        string label,
        bool passed,
        string reason) => new()
        {
            Code = code,
            Label = label,
            Passed = passed,
            Reason = reason,
        };
}

internal sealed record EvaluatedBoardItem(
    ManagedBoardItemSnapshot Snapshot,
    string? RepositoryKey,
    BoardItemMatchResult Match,
    IReadOnlyList<BoardItemDiagnosticCheck> Checks,
    IReadOnlyList<string> RecognizedRepositoryTags,
    string ReadyTag,
    string ReadyReworkTag,
    IReadOnlyList<string> Tags);

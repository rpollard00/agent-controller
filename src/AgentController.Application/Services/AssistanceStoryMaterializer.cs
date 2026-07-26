using System.Text.Json;
using AgentController.Domain;

namespace AgentController.Application;

/// <summary>Result of durably materializing one soaked assistance request.</summary>
public sealed record AssistanceStoryMaterializationResult
{
    public ReworkCycle Cycle { get; init; } = new();
    public WorkCandidate Story { get; init; } = new();
    public bool StoryWasCreated { get; init; }
}

/// <summary>
/// Coordinates restart-safe creation of a fresh story and Pending Assistance cycle.
/// The external story is reconciled by a stable correlation tag and is not made
/// eligible for discovery until its Pending cycle has been persisted locally.
/// </summary>
public sealed class AssistanceStoryMaterializer(
    IAgentRunStore runStore,
    IWorkItemStore workItemStore,
    IWorkSource workSource,
    IReworkCycleStore reworkCycleStore,
    IReworkFeedbackStore reworkFeedbackStore,
    IAssistanceStoryRelationshipResolver relationshipResolver,
    IPullRequestCommentCreator pullRequestCommentCreator)
{
    private const string CorrelationTagPrefix = "agent-assistance-correlation:";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Materializes one soaked Assistance feedback row idempotently.</summary>
    public async Task<AssistanceStoryMaterializationResult> MaterializeAsync(
        ReworkFeedback feedback,
        string? pullRequestTitle,
        CancellationToken cancellationToken)
    {
        Validate(feedback);
        var correlationId = feedback.CorrelationId!;
        var correlationTag = BuildCorrelationTag(correlationId);
        var existingCycle = await reworkCycleStore.GetByCorrelationIdAsync(
            correlationId,
            cancellationToken);
        var proposedCycleNumber = existingCycle?.CycleNumber
            ?? checked(
                await reworkCycleStore.GetMaxAssistanceCycleNumberAsync(
                    feedback.PullRequest,
                    cancellationToken)
                + 1);

        var story = await FindLocallyRecordedStoryAsync(feedback, cancellationToken);
        var storyWasCreated = false;

        if (story is null)
        {
            story = await FindStoryByCorrelationAsync(
                feedback,
                correlationTag,
                cancellationToken);

            if (story is null)
            {
                // Once an external receipt exists, an empty provider query is not proof
                // that the story disappeared. Stop and retry rather than risking a duplicate.
                if (!string.IsNullOrWhiteSpace(feedback.AssistanceStoryExternalId))
                {
                    throw new InvalidOperationException(
                        $"Assistance story '{feedback.AssistanceStoryExternalId}' has a persisted receipt "
                        + "but could not yet be reconciled from the work source."
                    );
                }

                story = await CreateStoryAsync(
                    feedback,
                    pullRequestTitle,
                    proposedCycleNumber,
                    correlationTag,
                    cancellationToken);
                storyWasCreated = true;

                // This receipt intentionally precedes the local upsert. If the process
                // stops at that boundary, the next pass reconciles by correlation tag.
                await reworkFeedbackStore.RecordAssistanceStoryAsync(
                    feedback.Id,
                    new AssistanceStoryReceipt
                    {
                        CorrelationId = correlationId,
                        ExternalId = story.ExternalId,
                        Url = story.ExternalUrl,
                    },
                    cancellationToken);
            }
        }

        EnsureStoryMatchesReceipt(story, feedback);
        var persistedStory = await workItemStore.UpsertAsync(story, cancellationToken);
        await reworkFeedbackStore.RecordAssistanceStoryAsync(
            feedback.Id,
            new AssistanceStoryReceipt
            {
                CorrelationId = correlationId,
                WorkItemId = persistedStory.Id,
                ExternalId = persistedStory.ExternalId,
                Url = persistedStory.ExternalUrl,
            },
            cancellationToken);

        var cycle = await reworkCycleStore.CreateAsync(
            new ReworkCycleCreateRequest
            {
                RequestMode = ReworkRequestMode.Assistance,
                PullRequest = feedback.PullRequest,
                WorkItemId = persistedStory.Id,
                CycleNumber = proposedCycleNumber,
                PriorRunId = feedback.OriginatingRunId,
                BranchName = feedback.PullRequest.SourceBranch,
                PullRequestUrl = feedback.PullRequest.PullRequestUrl,
                BaseCommitSha = feedback.PullRequest.SourceCommitSha,
                FeedbackBundleJson = feedback.FeedbackBundleJson,
                FeedbackBundleId = feedback.FeedbackBundleId,
                CorrelationId = correlationId,
            },
            cancellationToken);

        // Publishing is deliberately after cycle creation. Both provider publication
        // and the local upsert are idempotent, so a stop at either boundary is repairable.
        var publishedStory = await workSource.MakeAssistanceStoryReadyAsync(
            persistedStory,
            cancellationToken);
        publishedStory = await workItemStore.UpsertAsync(publishedStory, cancellationToken);

        await pullRequestCommentCreator.CreateAsync(
            feedback.PullRequest,
            new AssistanceLifecycleCommentRequest
            {
                CorrelationId = correlationId,
                Kind = AssistanceLifecycleCommentKind.Queued,
                AssistanceStoryId = publishedStory.ExternalId,
                AssistanceStoryUrl = publishedStory.ExternalUrl ?? string.Empty,
            },
            cancellationToken);

        await reworkFeedbackStore.MarkMaterializedAsync(feedback.Id, cancellationToken);

        return new AssistanceStoryMaterializationResult
        {
            Cycle = cycle,
            Story = publishedStory,
            StoryWasCreated = storyWasCreated,
        };
    }

    /// <summary>Stable provider tag used to reconcile a materialization receipt.</summary>
    public static string BuildCorrelationTag(string correlationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        return CorrelationTagPrefix + correlationId.Trim();
    }

    private async Task<WorkCandidate?> FindLocallyRecordedStoryAsync(
        ReworkFeedback feedback,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(feedback.AssistanceStoryWorkItemId))
            return null;

        var story = await workItemStore.GetByIdAsync(
            feedback.AssistanceStoryWorkItemId,
            cancellationToken);
        if (story is null)
        {
            throw new InvalidOperationException(
                $"Assistance story work item '{feedback.AssistanceStoryWorkItemId}' is missing locally."
            );
        }

        return story;
    }

    private async Task<WorkCandidate?> FindStoryByCorrelationAsync(
        ReworkFeedback feedback,
        string correlationTag,
        CancellationToken cancellationToken)
    {
        var candidates = await workSource.FindEligibleAsync(
            new WorkQuery
            {
                Tags = [correlationTag],
                MaxResults = 10,
            },
            cancellationToken);
        var matches = candidates
            .Where(candidate => candidate.Tags.Any(tag =>
                tag.Equals(correlationTag, StringComparison.OrdinalIgnoreCase)))
            .Where(candidate => candidate.RepoKey.Equals(
                feedback.PullRequest.RepositoryKey,
                StringComparison.OrdinalIgnoreCase))
            .Where(candidate => EnvironmentMatches(candidate, feedback.PullRequest.EnvironmentKey))
            .Take(2)
            .ToList();

        return matches.Count switch
        {
            0 => null,
            1 => matches[0],
            _ => throw new InvalidOperationException(
                $"More than one assistance story carries correlation tag '{correlationTag}'."
            ),
        };
    }

    private async Task<WorkCandidate> CreateStoryAsync(
        ReworkFeedback feedback,
        string? pullRequestTitle,
        int cycleNumber,
        string correlationTag,
        CancellationToken cancellationToken)
    {
        var threads = JsonSerializer.Deserialize<ReviewThread[]>(
            feedback.FeedbackBundleJson,
            JsonOptions) ?? [];
        var context = AssistanceStoryContextFormatter.Render(
            new AssistanceStoryContextRequest
            {
                PullRequestTitle = pullRequestTitle ?? string.Empty,
                PullRequest = feedback.PullRequest,
                CycleNumber = cycleNumber,
                QualifyingThreads = threads,
            });
        var originatingWorkItem = await ResolveOriginatingWorkItemAsync(
            feedback.OriginatingRunId,
            cancellationToken);
        var relationships = await relationshipResolver.ResolveAsync(
            new AssistanceStoryRelationshipRequest
            {
                PullRequest = feedback.PullRequest,
                OriginatingWorkItem = originatingWorkItem,
            },
            cancellationToken);
        var created = await workSource.CreateAssistanceStoryAsync(
            new CreateAssistanceStoryRequest
            {
                EnvironmentKey = feedback.PullRequest.EnvironmentKey,
                RepoKey = feedback.PullRequest.RepositoryKey,
                Title = context.Title,
                Description = context.Description,
                CorrelationTags = [correlationTag],
                Relations = relationships.Relations,
                ReadyForClaim = false,
            },
            cancellationToken);

        ArgumentException.ThrowIfNullOrWhiteSpace(created.ExternalId);
        if (created.Candidate is null)
        {
            throw new InvalidOperationException(
                "The work source did not return local candidate data for the assistance story."
            );
        }

        return created.Candidate with
        {
            ExternalId = created.ExternalId,
            ExternalUrl = string.IsNullOrWhiteSpace(created.Url)
                ? created.Candidate.ExternalUrl
                : created.Url,
        };
    }

    private async Task<ExternalWorkRef?> ResolveOriginatingWorkItemAsync(
        string? originatingRunId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(originatingRunId))
            return null;

        var run = await runStore.GetByIdAsync(originatingRunId, cancellationToken);
        if (string.IsNullOrWhiteSpace(run?.WorkItemId))
            return null;

        var workItem = await workItemStore.GetByIdAsync(run.WorkItemId, cancellationToken);
        return workItem is null ? null : ToExternalWorkRef(workItem);
    }

    private static ExternalWorkRef ToExternalWorkRef(WorkCandidate candidate)
    {
        var revision = candidate.SourceMetadata?.TryGetValue("revision", out var value) == true
            ? value
            : null;
        var environmentKey = candidate.SourceMetadata?.TryGetValue(
            "workSourceEnvironmentKey",
            out var key) == true
                ? key
                : null;
        return new ExternalWorkRef
        {
            Source = candidate.Source,
            ExternalId = candidate.ExternalId,
            Url = candidate.ExternalUrl,
            Revision = revision,
            EnvironmentKey = environmentKey,
        };
    }

    private static bool EnvironmentMatches(WorkCandidate candidate, string environmentKey)
    {
        if (string.IsNullOrWhiteSpace(environmentKey))
            return true;

        return candidate.SourceMetadata?.TryGetValue(
                "workSourceEnvironmentKey",
                out var candidateEnvironment) == true
            && candidateEnvironment.Equals(environmentKey, StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureStoryMatchesReceipt(
        WorkCandidate story,
        ReworkFeedback feedback)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(story.ExternalId);
        if (!string.IsNullOrWhiteSpace(feedback.AssistanceStoryExternalId)
            && !story.ExternalId.Equals(
                feedback.AssistanceStoryExternalId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The reconciled assistance story does not match the persisted external receipt."
            );
        }
    }

    private static void Validate(ReworkFeedback feedback)
    {
        ArgumentNullException.ThrowIfNull(feedback);
        if (feedback.RequestMode != ReworkRequestMode.Assistance)
        {
            throw new ArgumentException(
                "Only Assistance feedback can be materialized into a new story.",
                nameof(feedback));
        }

        if (feedback.Status != ReworkFeedbackStatus.Soaked)
        {
            throw new ArgumentException(
                "Assistance feedback must be Soaked before materialization.",
                nameof(feedback));
        }

        if (!feedback.PullRequest.HasCanonicalIdentity)
        {
            throw new ArgumentException(
                "Assistance feedback requires a canonical pull-request identity.",
                nameof(feedback));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(feedback.CorrelationId);
    }
}

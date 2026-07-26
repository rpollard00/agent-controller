using AgentController.Application.Results;
using AgentController.Domain;

namespace AgentController.Application.Queries;

/// <summary>Maps persisted runs and rework tracking records to dashboard cards.</summary>
internal static class RunCardFactory
{
    private const string PendingCategory = "pending";

    public static string ClassifyCategory(RunLifecycleState status) =>
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

    public static RunCardItem CreateRunCard(
        AgentRunHandle run,
        WorkCandidate? workItem,
        string? repositoryUrl,
        LifecycleEvent? latestEvent,
        ReworkCycle? cycle,
        ReworkFeedback? feedback
    ) =>
        new()
        {
            Id = run.RunId,
            Kind = "run",
            Status = run.Status.ToString(),
            Category = ClassifyCategory(run.Status),
            WorkItemTitle = workItem?.Title,
            WorkItemUrl = workItem?.ExternalUrl,
            WorkItemSource = workItem?.Source,
            RepoKey = workItem?.RepoKey,
            RepositoryUrl = repositoryUrl,
            RuntimeType = run.RuntimeType,
            RuntimeProfileName = run.RuntimeProfileName,
            EnvironmentProviderType = run.EnvironmentProviderType,
            RunAttempt = run.RunAttempt,
            RequestMode = cycle?.RequestMode,
            PullRequest = cycle?.PullRequest,
            CycleNumber = cycle?.CycleNumber,
            AssistanceStoryWorkItemId = feedback?.AssistanceStoryWorkItemId
                ?? (cycle?.RequestMode == ReworkRequestMode.Assistance ? cycle.WorkItemId : null),
            AssistanceStoryExternalId = feedback?.AssistanceStoryExternalId
                ?? (cycle?.RequestMode == ReworkRequestMode.Assistance ? workItem?.ExternalId : null),
            AssistanceStoryUrl = feedback?.AssistanceStoryUrl
                ?? (cycle?.RequestMode == ReworkRequestMode.Assistance ? workItem?.ExternalUrl : null),
            FeedbackStatus = feedback?.Status,
            CycleStatus = cycle?.Status,
            ConsumingRunId = cycle?.NewRunId,
            LastEventType = latestEvent?.EventType,
            LastEventMessage = latestEvent?.Message,
            LastEventAt = latestEvent?.CreatedAt,
            CreatedAt = run.CreatedAt,
            UpdatedAt = run.UpdatedAt,
        };

    public static RunCardItem CreateTrackingCard(
        ReworkFeedback feedback,
        ReworkCycle? cycle,
        AgentRunHandle? associatedRun,
        WorkCandidate? workItem,
        string? repositoryUrl
    )
    {
        var threadLabel = feedback.ThreadCount == 1 ? "thread" : "threads";
        var isAssistance = feedback.RequestMode == ReworkRequestMode.Assistance;

        return new RunCardItem
        {
            Id = feedback.Id,
            Kind = "rework-soak",
            Status = ResolveTrackingStatus(feedback, cycle),
            Category = PendingCategory,
            WorkItemTitle = workItem?.Title,
            WorkItemUrl = workItem?.ExternalUrl,
            WorkItemSource = workItem?.Source,
            RepoKey = workItem?.RepoKey ?? feedback.PullRequest.RepositoryKey,
            RepositoryUrl = repositoryUrl,
            RuntimeType = associatedRun?.RuntimeType,
            RuntimeProfileName = associatedRun?.RuntimeProfileName,
            EnvironmentProviderType = associatedRun?.EnvironmentProviderType,
            RunAttempt = associatedRun?.RunAttempt ?? 1,
            RequestMode = feedback.RequestMode,
            PullRequest = feedback.PullRequest,
            CycleNumber = cycle?.CycleNumber,
            AssistanceStoryWorkItemId = feedback.AssistanceStoryWorkItemId,
            AssistanceStoryExternalId = feedback.AssistanceStoryExternalId,
            AssistanceStoryUrl = feedback.AssistanceStoryUrl,
            FeedbackStatus = feedback.Status,
            CycleStatus = cycle?.Status,
            ConsumingRunId = cycle?.NewRunId,
            LastEventType = ResolveTrackingEventType(feedback, cycle),
            LastEventMessage = feedback.Status == ReworkFeedbackStatus.Watching
                ? $"{feedback.ThreadCount} feedback {threadLabel} awaiting soak"
                : ResolveTrackingMessage(feedback, cycle, isAssistance),
            LastEventAt = feedback.Status == ReworkFeedbackStatus.Watching
                ? feedback.LastQualifyingCommentAt
                : feedback.UpdatedAt,
            CreatedAt = feedback.CreatedAt,
            UpdatedAt = feedback.UpdatedAt,
        };
    }

    private static string ResolveTrackingStatus(ReworkFeedback feedback, ReworkCycle? cycle) =>
        feedback.Status switch
        {
            ReworkFeedbackStatus.Watching => feedback.RequestMode == ReworkRequestMode.Assistance
                ? "Assistance feedback soaking"
                : "Rework feedback soaking",
            ReworkFeedbackStatus.Soaked => feedback.RequestMode == ReworkRequestMode.Assistance
                ? "Assistance feedback soaked"
                : "Rework feedback soaked",
            ReworkFeedbackStatus.Materialized when cycle?.Status == ReworkCycleStatus.Consumed =>
                feedback.RequestMode == ReworkRequestMode.Assistance
                    ? "Assistance rework in progress"
                    : "Rework in progress",
            ReworkFeedbackStatus.Materialized => feedback.RequestMode == ReworkRequestMode.Assistance
                ? "Assistance story queued"
                : "Rework queued",
            _ => feedback.Status.ToString(),
        };

    private static string ResolveTrackingEventType(
        ReworkFeedback feedback,
        ReworkCycle? cycle
    ) =>
        feedback.Status switch
        {
            ReworkFeedbackStatus.Watching => feedback.RequestMode == ReworkRequestMode.Assistance
                ? "assistance.feedback.soaking"
                : "rework.feedback.soaking",
            ReworkFeedbackStatus.Soaked => feedback.RequestMode == ReworkRequestMode.Assistance
                ? "assistance.feedback.soaked"
                : "rework.feedback.soaked",
            ReworkFeedbackStatus.Materialized when cycle?.Status == ReworkCycleStatus.Consumed =>
                feedback.RequestMode == ReworkRequestMode.Assistance
                    ? "assistance.cycle.consumed"
                    : "rework.cycle.consumed",
            ReworkFeedbackStatus.Materialized => feedback.RequestMode == ReworkRequestMode.Assistance
                ? "assistance.story.queued"
                : "rework.cycle.queued",
            _ => "rework.feedback.updated",
        };

    private static string ResolveTrackingMessage(
        ReworkFeedback feedback,
        ReworkCycle? cycle,
        bool isAssistance
    )
    {
        if (feedback.Status == ReworkFeedbackStatus.Soaked)
        {
            return $"{feedback.ThreadCount} feedback {(feedback.ThreadCount == 1 ? "thread" : "threads")} ready for materialization";
        }

        if (!string.IsNullOrWhiteSpace(cycle?.NewRunId))
        {
            return $"{(isAssistance ? "Assistance" : "Rework")} cycle consumed by run {cycle.NewRunId}";
        }

        var storyId = feedback.AssistanceStoryExternalId ?? feedback.AssistanceStoryWorkItemId;
        return isAssistance && !string.IsNullOrWhiteSpace(storyId)
            ? $"Assistance story {storyId} queued for rework"
            : "Rework cycle queued";
    }
}

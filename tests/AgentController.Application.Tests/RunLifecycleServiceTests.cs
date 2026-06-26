using AgentController.Application;
using AgentController.Application.Abstractions;
using AgentController.Application.Services;
using AgentController.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentController.Application.Tests;

/// <summary>
/// Minimal null logger for unit tests. <c>NullLogger&lt;T&gt;</c> is not available
/// in the Abstractions package for net10.0, so we provide a stub here.
/// </summary>
internal sealed class NullLogger<T> : ILogger<T>, ILogger
{
    public static NullLogger<T> Instance { get; } = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null!;
    public bool IsEnabled(LogLevel level) => false;
    public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
}

/// <summary>
/// Comprehensive tests for <see cref="RunLifecycleService"/>, covering:
/// - State transition validation (legal/prohibited transitions)
/// - Terminal state rejection
/// - Runtime event idempotency (duplicate eventId)
/// - Event type dispatch (all supported types)
/// - Regression prevention (accepted on progressing/terminal runs)
/// - Stale recovery
/// - Unsupported completion outcomes
/// - Missing required fields
/// </summary>
public class RunLifecycleServiceTests
{
    private readonly InMemoryAgentRunStore _runStore;
    private readonly InMemoryLifecycleEventStore _eventStore;
    private readonly InMemoryWorkItemStore _workItemStore;
    private readonly RunLifecycleService _service;
    private readonly IOptionsMonitor<WorkSourceOptionsView> _workSourceOptions;

    public RunLifecycleServiceTests()
    {
        _runStore = new InMemoryAgentRunStore();
        _eventStore = new InMemoryLifecycleEventStore();
        _workItemStore = new InMemoryWorkItemStore();
        _workSourceOptions = new TestOptionsMonitor<WorkSourceOptionsView>(new WorkSourceOptionsView
        {
            ActiveState = "Active",
            CompletedState = "Resolved",
        });
        _service = new RunLifecycleService(
            NullLogger<RunLifecycleService>.Instance,
            _runStore, _eventStore, _workItemStore,
            new StubWorkSource(), _workSourceOptions);
    }

    // ── CreateRunForWorkItemAsync ──────────────────────────────────

    [Fact]
    public async Task CreateRunForWorkItem_ThrowsWhenWorkItemNotFound()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.CreateRunForWorkItemAsync("nonexistent", "worker-1", CancellationToken.None));

        Assert.Contains("not found", ex.Message);
    }

    [Fact]
    public async Task CreateRunForWorkItem_CreatesRunInClaimedState()
    {
        var wi = await _workItemStore.CreateAsync(
            new CreateWorkItemRequest { RepoKey = "repo", Title = "Test" }, CancellationToken.None);

        var run = await _service.CreateRunForWorkItemAsync(wi.Id, "worker-1", CancellationToken.None);

        Assert.Equal(RunLifecycleState.Claimed, run.Status);
        Assert.Equal(wi.Id, run.WorkItemId);

        // Verify lifecycle event was appended
        var events = await _eventStore.ListByRunIdAsync(run.RunId, CancellationToken.None);
        Assert.Single(events);
        Assert.Equal("controller.claimed", events[0].EventType);
    }

    // ── CreateRunForWorkItemAsync — upsert + Azure integration ────

    [Fact]
    public async Task UpsertAsync_PersistsAndRetrievesSourceMetadata()
    {
        // Upsert a candidate with SourceMetadata (simulating Azure DevOps discovery)
        var candidate = new WorkCandidate
        {
            Id = "wi_42",
            ExternalId = "42",
            Source = "AzureDevOpsBoards",
            Title = "Fix login bug",
            Description = "Users cannot log in",
            Status = "New",
            Priority = 1,
            RepoKey = "example-service",
            Tags = new[] { "agent-ready", "repo:example-service" },
            ExternalUrl = "https://dev.azure.com/org/project/_workitems/edit/42",
            SourceMetadata = new Dictionary<string, string>
            {
                ["revision"] = "3",
                ["areaPath"] = @"Project\TeamA",
                ["iterationPath"] = @"Project\Sprint 1",
                ["workItemType"] = "Bug",
            },
        };

        var persisted = await _workItemStore.UpsertAsync(candidate, CancellationToken.None);

        // Verify the upserted candidate has an ID and preserves metadata
        Assert.Equal("wi_42", persisted.Id);
        Assert.Equal("AzureDevOpsBoards", persisted.Source);

        // Retrieve by ID to verify SourceMetadata is round-tripped
        var retrieved = await _workItemStore.GetByIdAsync(persisted.Id, CancellationToken.None);
        Assert.NotNull(retrieved);
        Assert.NotNull(retrieved.SourceMetadata);
        Assert.Equal("3", retrieved.SourceMetadata["revision"]);
        Assert.Equal(@"Project\TeamA", retrieved.SourceMetadata["areaPath"]);
        Assert.Equal(@"Project\Sprint 1", retrieved.SourceMetadata["iterationPath"]);
        Assert.Equal("Bug", retrieved.SourceMetadata["workItemType"]);

        // Upsert again with updated metadata — should update existing
        var updated = candidate with
        {
            Title = "Fix login bug (updated)",
            SourceMetadata = new Dictionary<string, string>
            {
                ["revision"] = "5",
                ["areaPath"] = @"Project\TeamA",
            },
        };
        await _workItemStore.UpsertAsync(updated, CancellationToken.None);

        var retrievedAgain = await _workItemStore.GetByIdAsync(persisted.Id, CancellationToken.None);
        Assert.NotNull(retrievedAgain);
        Assert.Equal("Fix login bug (updated)", retrievedAgain.Title);
        Assert.NotNull(retrievedAgain.SourceMetadata);
        Assert.Equal("5", retrievedAgain.SourceMetadata["revision"]);
        // iterationPath should be from original since we didn't set it in the update
        Assert.False(retrievedAgain.SourceMetadata.ContainsKey("iterationPath"));
    }

    [Fact]
    public async Task UpsertRemoteCandidate_CreateRunSucceeds()
    {
        // Simulate the Azure DevOps discovery → upsert → claim → run creation path.
        // This tests that the integration gap (remote WorkCandidate not in IWorkItemStore)
        // is closed by UpsertAsync.
        var candidate = new WorkCandidate
        {
            Id = "wi_99",
            ExternalId = "99",
            Source = "AzureDevOpsBoards",
            Title = "Add retry logic",
            Description = "Implement retry handling for transient failures",
            Status = "Approved",
            Priority = 2,
            RepoKey = "backend",
            Tags = new[] { "agent-ready", "repo:backend" },
            ExternalUrl = "https://dev.azure.com/org/project/_workitems/edit/99",
            SourceMetadata = new Dictionary<string, string>
            {
                ["revision"] = "1",
                ["workItemType"] = "User Story",
            },
        };

        // Step 1: Upsert into local persistence (what PollingWorker now does)
        var persisted = await _workItemStore.UpsertAsync(candidate, CancellationToken.None);
        Assert.Equal("AzureDevOpsBoards", persisted.Source);
        Assert.Equal("99", persisted.ExternalId);

        // Step 2: Create a run for the upserted work item
        // (previously this would fail with "not found")
        var run = await _service.CreateRunForWorkItemAsync(
            persisted.Id, "worker-1", CancellationToken.None);

        Assert.NotNull(run);
        Assert.Equal(persisted.Id, run.WorkItemId);
        Assert.Equal(RunLifecycleState.Claimed, run.Status);
    }

    [Fact]
    public async Task AzureSourcedWorkItem_ProjectsCommentsOnTransition()
    {
        // Verify that transitioning an Azure-sourced work item triggers
        // comment projection to the work source via IWorkSource.AddCommentAsync.
        var stubWorkSource = new StubWorkSource();
        var serviceWithStub = new RunLifecycleService(
            NullLogger<RunLifecycleService>.Instance, _runStore, _eventStore, _workItemStore, stubWorkSource, _workSourceOptions);

        // Upsert an Azure candidate with SourceMetadata (revision)
        var candidate = new WorkCandidate
        {
            Id = "wi_55",
            ExternalId = "55",
            Source = "AzureDevOpsBoards",
            Title = "Projection test",
            Description = "Test that status is projected",
            Status = "New",
            RepoKey = "test-repo",
            Tags = new[] { "agent-ready" },
            ExternalUrl = "https://dev.azure.com/org/project/_workitems/edit/55",
            SourceMetadata = new Dictionary<string, string>
            {
                ["revision"] = "2",
            },
        };
        await _workItemStore.UpsertAsync(candidate, CancellationToken.None);

        // Create a run and transition to AwaitingResult. The AgentRunning
        // milestone triggers a comment via BuildExternalProjection.
        var run = await serviceWithStub.CreateRunForWorkItemAsync(
            "wi_55", "worker-1", CancellationToken.None);

        await serviceWithStub.TransitionAsync(run.RunId, RunLifecycleState.EnvironmentProvisioning, CancellationToken.None);
        await serviceWithStub.TransitionAsync(run.RunId, RunLifecycleState.EnvironmentReady, CancellationToken.None);
        await serviceWithStub.TransitionAsync(run.RunId, RunLifecycleState.RepositoryCloning, CancellationToken.None);
        await serviceWithStub.TransitionAsync(run.RunId, RunLifecycleState.RepositoryReady, CancellationToken.None);
        await serviceWithStub.TransitionAsync(run.RunId, RunLifecycleState.ContextInjected, CancellationToken.None);
        await serviceWithStub.TransitionAsync(run.RunId, RunLifecycleState.AgentStarting, CancellationToken.None);
        await serviceWithStub.TransitionAsync(run.RunId, RunLifecycleState.AgentRunning, CancellationToken.None);

        // AgentRunning milestone triggers a comment: "Agent runtime is now executing."
        // The Claimed projection (agent-active tag) happens via the Azure client's
        // TryClaimWorkItemAsync, not through TransitionAsync, so we only check comments.
        Assert.NotEmpty(stubWorkSource.Comments);

        // All comments should reference the correct work item with SourceMetadata revision
        Assert.All(stubWorkSource.Comments, c =>
        {
            Assert.Equal("AzureDevOpsBoards", c.WorkRef.Source);
            Assert.Equal("55", c.WorkRef.ExternalId);
            Assert.Equal("2", c.WorkRef.Revision);
        });

        Assert.Contains(stubWorkSource.Comments, c =>
            c.Comment.Contains("Agent runtime is now executing", StringComparison.Ordinal));
    }

    // ── TransitionAsync — allowed transitions ──────────────────────

    [Fact]
    public async Task TransitionAsync_LegalProgressionSucceeds()
    {
        var run = await CreateRunAsync();

        // Claimed → EnvironmentProvisioning → EnvironmentReady
        await _service.TransitionAsync(run.RunId, RunLifecycleState.EnvironmentProvisioning, CancellationToken.None);
        await _service.TransitionAsync(run.RunId, RunLifecycleState.EnvironmentReady, CancellationToken.None);

        var updated = await _runStore.GetByIdAsync(run.RunId, CancellationToken.None);
        Assert.Equal(RunLifecycleState.EnvironmentReady, updated!.Status);

        // Verify lifecycle events for each transition
        var events = await _eventStore.ListByRunIdAsync(run.RunId, CancellationToken.None);
        Assert.Contains(events, e => e.EventType == "controller.state_transition");
    }

    [Fact]
    public async Task TransitionAsync_IdempotentToSameState()
    {
        var run = await CreateRunAsync();

        await _service.TransitionAsync(run.RunId, RunLifecycleState.EnvironmentProvisioning, CancellationToken.None);
        // Transition to same state (idempotent)
        await _service.TransitionAsync(run.RunId, RunLifecycleState.EnvironmentProvisioning, CancellationToken.None);

        var updated = await _runStore.GetByIdAsync(run.RunId, CancellationToken.None);
        Assert.Equal(RunLifecycleState.EnvironmentProvisioning, updated!.Status);
    }

    // ── TransitionAsync — prohibited transitions ───────────────────

    [Fact]
    public async Task TransitionAsync_RejectsIllegalTransition()
    {
        var run = await CreateRunAsync();

        // Claimed → RepositoryReady is illegal (must go through EnvironmentProvisioning first)
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.TransitionAsync(run.RunId, RunLifecycleState.RepositoryReady, CancellationToken.None));

        Assert.Contains("not allowed", ex.Message);
        // State must not have changed
        var unchanged = await _runStore.GetByIdAsync(run.RunId, CancellationToken.None);
        Assert.Equal(RunLifecycleState.Claimed, unchanged!.Status);
    }

    [Fact]
    public async Task TransitionAsync_RejectsTerminalStateTransition()
    {
        var run = await CreateRunAsync();

        // Manually set to terminal
        await _runStore.ForceUpdateStatusAsync(run.RunId, RunLifecycleState.Failed);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.TransitionAsync(run.RunId, RunLifecycleState.CleanupPending, CancellationToken.None));

        Assert.Contains("terminal state", ex.Message);
    }

    [Fact]
    public async Task TransitionAsync_RejectsNonexistentRun()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.TransitionAsync("nonexistent", RunLifecycleState.Claimed, CancellationToken.None));

        Assert.Contains("not found", ex.Message);
    }

    // ── TransitionAsync — cancellation allowed from any non-terminal state ──

    [Fact]
    public async Task TransitionAsync_CancellationAllowedFromAnyNonTerminalState()
    {
        var run = await CreateRunAsync();
        await _service.TransitionAsync(run.RunId, RunLifecycleState.EnvironmentProvisioning, CancellationToken.None);
        await _service.TransitionAsync(run.RunId, RunLifecycleState.EnvironmentReady, CancellationToken.None);
        await _service.TransitionAsync(run.RunId, RunLifecycleState.RepositoryCloning, CancellationToken.None);

        // Cancel from RepositoryCloning — allowed
        await _service.TransitionAsync(run.RunId, RunLifecycleState.Cancelled, CancellationToken.None);

        var updated = await _runStore.GetByIdAsync(run.RunId, CancellationToken.None);
        Assert.Equal(RunLifecycleState.Cancelled, updated!.Status);
    }

    // ── IsTerminal ─────────────────────────────────────────────────

    [Theory]
    [InlineData(RunLifecycleState.Completed, true)]
    [InlineData(RunLifecycleState.Failed, true)]
    [InlineData(RunLifecycleState.Cancelled, true)]
    [InlineData(RunLifecycleState.CleanedUp, true)]
    [InlineData(RunLifecycleState.Queued, false)]
    [InlineData(RunLifecycleState.Claimed, false)]
    [InlineData(RunLifecycleState.AwaitingResult, false)]
    [InlineData(RunLifecycleState.NeedsHuman, false)]
    [InlineData(RunLifecycleState.PrOpened, false)]
    public void IsTerminal_ReturnsCorrectValue(RunLifecycleState state, bool expected)
    {
        Assert.Equal(expected, _service.IsTerminal(state));
    }

    // ── IngestRuntimeEventAsync — idempotency ──────────────────────

    [Fact]
    public async Task IngestRuntimeEvent_RejectsDuplicateEventId()
    {
        var run = await CreateRunAsync(advanceTo: RunLifecycleState.AwaitingResult);
        var evt = CreateEvent(run.RunId, "evt_001", RuntimeEventTypes.Status, "Update 1");

        await _service.IngestRuntimeEventAsync(evt, CancellationToken.None);

        // Same eventId again — must be rejected
        var duplicate = CreateEvent(run.RunId, "evt_001", RuntimeEventTypes.Status, "Update 2");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.IngestRuntimeEventAsync(duplicate, CancellationToken.None));

        Assert.Contains("already been processed", ex.Message);

        // Verify only one lifecycle event was recorded
        var events = await _eventStore.ListByRunIdAsync(run.RunId, CancellationToken.None);
        var runtimeEvents = events.Where(e => e.EventId == "evt_001").ToList();
        Assert.Single(runtimeEvents);
        Assert.Equal("Update 1", runtimeEvents[0].Message);
    }

    // ── IngestRuntimeEventAsync — missing required fields ──────────

    [Fact]
    public async Task IngestRuntimeEvent_RejectsMissingEventId()
    {
        var evt = new RuntimeEvent
        {
            EventId = "",
            RunId = "run_1",
            EventType = RuntimeEventTypes.Status,
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.IngestRuntimeEventAsync(evt, CancellationToken.None));

        Assert.Contains("eventId", ex.Message);
    }

    [Fact]
    public async Task IngestRuntimeEvent_RejectsMissingRunId()
    {
        var evt = new RuntimeEvent
        {
            EventId = "evt_001",
            RunId = "",
            EventType = RuntimeEventTypes.Status,
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.IngestRuntimeEventAsync(evt, CancellationToken.None));

        Assert.Contains("runId", ex.Message);
    }

    [Fact]
    public async Task IngestRuntimeEvent_RejectsMissingEventType()
    {
        var evt = new RuntimeEvent
        {
            EventId = "evt_001",
            RunId = "run_1",
            EventType = "",
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.IngestRuntimeEventAsync(evt, CancellationToken.None));

        Assert.Contains("eventType", ex.Message);
    }

    // ── IngestRuntimeEventAsync — terminal state rejection ─────────

    [Fact]
    public async Task IngestRuntimeEvent_RejectsTerminalRun()
    {
        var run = await CreateRunAsync(advanceTo: RunLifecycleState.AwaitingResult);

        // Force the run to a terminal state
        await _runStore.ForceUpdateStatusAsync(run.RunId, RunLifecycleState.Completed);

        var evt = CreateEvent(run.RunId, "evt_001", RuntimeEventTypes.Status, "Late update");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.IngestRuntimeEventAsync(evt, CancellationToken.None));

        Assert.Contains("terminal state", ex.Message);
    }

    // ── IngestRuntimeEventAsync — nonexistent run ──────────────────

    [Fact]
    public async Task IngestRuntimeEvent_RejectsNonexistentRun()
    {
        var evt = CreateEvent("run_nonexistent", "evt_001", RuntimeEventTypes.Status, "Update");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.IngestRuntimeEventAsync(evt, CancellationToken.None));

        Assert.Contains("not found", ex.Message);
    }

    // ── IngestRuntimeEventAsync — runtime.accepted ─────────────────

    [Fact]
    public async Task IngestAccepted_AdvancesRunToAgentRunning()
    {
        // Start from Claimed (well before AgentRunning)
        var run = await CreateRunAsync(); // Claimed

        var evt = CreateEvent(run.RunId, "evt_accepted", RuntimeEventTypes.Accepted,
            "Runtime accepted", runtimeRunId: "pi_123");
        await _service.IngestRuntimeEventAsync(evt, CancellationToken.None);

        var updated = await _runStore.GetByIdAsync(run.RunId, CancellationToken.None);
        Assert.Equal(RunLifecycleState.AgentRunning, updated!.Status);
        Assert.Equal("pi_123", updated.RuntimeRunId);
    }

    [Fact]
    public async Task IngestAccepted_OnAwaitingResult_ToleratedAsInformational()
    {
        // The PollingWorker advances a run to AwaitingResult synchronously,
        // while a real pi runtime takes seconds to boot before POSTing
        // accepted. So accepted frequently lands on an AwaitingResult run.
        // It must be tolerated (no throw), must not regress the state, and
        // must still record the runtime run id + refresh the heartbeat.
        var run = await CreateRunAsync(advanceTo: RunLifecycleState.AwaitingResult);

        var evt = CreateEvent(run.RunId, "evt_accepted", RuntimeEventTypes.Accepted,
            "Runtime accepted", runtimeRunId: "pi_123");

        await _service.IngestRuntimeEventAsync(evt, CancellationToken.None);

        var updated = await _runStore.GetByIdAsync(run.RunId, CancellationToken.None);
        Assert.Equal(RunLifecycleState.AwaitingResult, updated!.Status); // state unchanged
        Assert.Equal("pi_123", updated.RuntimeRunId); // runtime id recorded
        Assert.Equal(evt.OccurredAt, updated.LastHeartbeatAt); // heartbeat refreshed
    }

    [Fact]
    public async Task IngestAccepted_OnNeedsHuman_ToleratedAsInformational()
    {
        // NeedsHuman is past AgentRunning and is only reachable via a
        // runtime.needs_human event, so seed it realistically: advance to
        // AwaitingResult, ingest needs_human, then send accepted.
        var run = await CreateRunAsync(advanceTo: RunLifecycleState.AwaitingResult);
        await _service.IngestRuntimeEventAsync(
            CreateEvent(run.RunId, "evt_nh", RuntimeEventTypes.NeedsHuman, "needs a human"),
            CancellationToken.None);

        var seeded = await _runStore.GetByIdAsync(run.RunId, CancellationToken.None);
        Assert.Equal(RunLifecycleState.NeedsHuman, seeded!.Status);

        // A stray accepted on NeedsHuman is harmless and must be tolerated
        // (no throw), leaving the run in NeedsHuman and recording the runtime id.
        var evt = CreateEvent(run.RunId, "evt_accepted", RuntimeEventTypes.Accepted,
            "Accepted", runtimeRunId: "pi_456");

        await _service.IngestRuntimeEventAsync(evt, CancellationToken.None);

        var updated = await _runStore.GetByIdAsync(run.RunId, CancellationToken.None);
        Assert.Equal(RunLifecycleState.NeedsHuman, updated!.Status); // state unchanged
        Assert.Equal("pi_456", updated.RuntimeRunId);
    }

    // ── IngestRuntimeEventAsync — runtime.heartbeat ────────────────

    [Fact]
    public async Task IngestHeartbeat_UpdatesLastHeartbeat()
    {
        var run = await CreateRunAsync(advanceTo: RunLifecycleState.AwaitingResult);
        var now = DateTimeOffset.UtcNow;

        var evt = new RuntimeEvent
        {
            EventId = "evt_hb_1",
            RunId = run.RunId,
            EventType = RuntimeEventTypes.Heartbeat,
            OccurredAt = now,
        };

        await _service.IngestRuntimeEventAsync(evt, CancellationToken.None);

        var updated = await _runStore.GetByIdAsync(run.RunId, CancellationToken.None);
        Assert.Equal(now, updated!.LastHeartbeatAt);
        // State must remain unchanged
        Assert.Equal(RunLifecycleState.AwaitingResult, updated.Status);
    }

    // ── IngestRuntimeEventAsync — runtime.status ───────────────────

    [Fact]
    public async Task IngestStatus_RecordsEventWithoutStateChange()
    {
        var run = await CreateRunAsync(advanceTo: RunLifecycleState.AwaitingResult);

        var evt = CreateEvent(run.RunId, "evt_status_1", RuntimeEventTypes.Status,
            "Running unit tests", runtimeRunId: "pi_456");

        await _service.IngestRuntimeEventAsync(evt, CancellationToken.None);

        var updated = await _runStore.GetByIdAsync(run.RunId, CancellationToken.None);
        Assert.Equal(RunLifecycleState.AwaitingResult, updated!.Status);
        Assert.Equal("pi_456", updated.RuntimeRunId);
    }

    // ── IngestRuntimeEventAsync — runtime.completed ────────────────

    [Fact]
    public async Task IngestCompleted_PullRequestOpened_TransitionsToPrOpened()
    {
        var run = await CreateRunAsync(advanceTo: RunLifecycleState.AwaitingResult);
        var now = DateTimeOffset.UtcNow;

        var evt = new RuntimeEvent
        {
            EventId = "evt_comp_1",
            RunId = run.RunId,
            EventType = RuntimeEventTypes.Completed,
            OccurredAt = now,
            Message = "Completed with PR",
            Payload = new Dictionary<string, object?>
            {
                ["outcome"] = CompletionOutcomes.PullRequestOpened,
                ["pullRequestUrl"] = "https://dev.azure.com/pr/123",
                ["branchName"] = "agent/123-fix",
                ["summary"] = "Fixed the bug",
            },
        };

        await _service.IngestRuntimeEventAsync(evt, CancellationToken.None);

        var updated = await _runStore.GetByIdAsync(run.RunId, CancellationToken.None);
        Assert.Equal(RunLifecycleState.PrOpened, updated!.Status);
        Assert.Equal("https://dev.azure.com/pr/123", updated.PullRequestUrl);
        Assert.Equal("agent/123-fix", updated.BranchName);
        Assert.Equal("Fixed the bug", updated.ResultSummary);
        Assert.Equal(now, updated.FinishedAt);
    }

    [Fact]
    public async Task IngestCompleted_BranchPushed_TransitionsToBranchPushed()
    {
        var run = await CreateRunAsync(advanceTo: RunLifecycleState.AwaitingResult);

        var evt = new RuntimeEvent
        {
            EventId = "evt_comp_bp",
            RunId = run.RunId,
            EventType = RuntimeEventTypes.Completed,
            Payload = new Dictionary<string, object?> { ["outcome"] = CompletionOutcomes.BranchPushed },
        };

        await _service.IngestRuntimeEventAsync(evt, CancellationToken.None);

        var updated = await _runStore.GetByIdAsync(run.RunId, CancellationToken.None);
        Assert.Equal(RunLifecycleState.BranchPushed, updated!.Status);
    }

    [Fact]
    public async Task IngestCompleted_PatchCreated_TransitionsToCompleted()
    {
        var run = await CreateRunAsync(advanceTo: RunLifecycleState.AwaitingResult);

        var evt = new RuntimeEvent
        {
            EventId = "evt_comp_pc",
            RunId = run.RunId,
            EventType = RuntimeEventTypes.Completed,
            Payload = new Dictionary<string, object?> { ["outcome"] = CompletionOutcomes.PatchCreated },
        };

        await _service.IngestRuntimeEventAsync(evt, CancellationToken.None);

        var updated = await _runStore.GetByIdAsync(run.RunId, CancellationToken.None);
        Assert.Equal(RunLifecycleState.Completed, updated!.Status);
    }

    [Fact]
    public async Task IngestCompleted_NoChangesNeeded_TransitionsToCompleted()
    {
        var run = await CreateRunAsync(advanceTo: RunLifecycleState.AwaitingResult);

        var evt = new RuntimeEvent
        {
            EventId = "evt_comp_nc",
            RunId = run.RunId,
            EventType = RuntimeEventTypes.Completed,
            Payload = new Dictionary<string, object?> { ["outcome"] = CompletionOutcomes.NoChangesNeeded },
        };

        await _service.IngestRuntimeEventAsync(evt, CancellationToken.None);

        var updated = await _runStore.GetByIdAsync(run.RunId, CancellationToken.None);
        Assert.Equal(RunLifecycleState.Completed, updated!.Status);
    }

    [Fact]
    public async Task IngestCompleted_NeedsHuman_TransitionsToNeedsHuman()
    {
        var run = await CreateRunAsync(advanceTo: RunLifecycleState.AwaitingResult);

        var evt = new RuntimeEvent
        {
            EventId = "evt_comp_nh",
            RunId = run.RunId,
            EventType = RuntimeEventTypes.Completed,
            Payload = new Dictionary<string, object?> { ["outcome"] = CompletionOutcomes.NeedsHuman },
        };

        await _service.IngestRuntimeEventAsync(evt, CancellationToken.None);

        var updated = await _runStore.GetByIdAsync(run.RunId, CancellationToken.None);
        Assert.Equal(RunLifecycleState.NeedsHuman, updated!.Status);
    }

    [Fact]
    public async Task IngestCompleted_FailedOutcome_TransitionsToFailed()
    {
        var run = await CreateRunAsync(advanceTo: RunLifecycleState.AwaitingResult);

        var evt = new RuntimeEvent
        {
            EventId = "evt_comp_f",
            RunId = run.RunId,
            EventType = RuntimeEventTypes.Completed,
            Payload = new Dictionary<string, object?> { ["outcome"] = CompletionOutcomes.Failed },
        };

        await _service.IngestRuntimeEventAsync(evt, CancellationToken.None);

        var updated = await _runStore.GetByIdAsync(run.RunId, CancellationToken.None);
        Assert.Equal(RunLifecycleState.Failed, updated!.Status);
    }

    [Fact]
    public async Task IngestCompleted_UnsupportedOutcome_Throws()
    {
        var run = await CreateRunAsync(advanceTo: RunLifecycleState.AwaitingResult);

        var evt = new RuntimeEvent
        {
            EventId = "evt_bad_outcome",
            RunId = run.RunId,
            EventType = RuntimeEventTypes.Completed,
            Payload = new Dictionary<string, object?> { ["outcome"] = "random_gibberish" },
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.IngestRuntimeEventAsync(evt, CancellationToken.None));

        Assert.Contains("Unsupported completion outcome", ex.Message);
        Assert.Contains("random_gibberish", ex.Message);
    }

    // ── IngestRuntimeEventAsync — runtime.failed ───────────────────

    [Fact]
    public async Task IngestFailed_TransitionsToFailed()
    {
        var run = await CreateRunAsync(advanceTo: RunLifecycleState.AwaitingResult);
        var now = DateTimeOffset.UtcNow;

        var evt = new RuntimeEvent
        {
            EventId = "evt_fail_1",
            RunId = run.RunId,
            EventType = RuntimeEventTypes.Failed,
            OccurredAt = now,
            Severity = EventSeverity.Error,
            Message = "Tests failed after implementation",
            Payload = new Dictionary<string, object?>
            {
                ["reason"] = "tests_failed",
                ["summary"] = "Three tests failed",
            },
        };

        await _service.IngestRuntimeEventAsync(evt, CancellationToken.None);

        var updated = await _runStore.GetByIdAsync(run.RunId, CancellationToken.None);
        Assert.Equal(RunLifecycleState.Failed, updated!.Status);
        Assert.Contains("Tests failed after implementation", updated.Error);
        Assert.Equal(now, updated.FinishedAt);
    }

    // ── IngestRuntimeEventAsync — runtime.needs_human ──────────────

    [Fact]
    public async Task IngestNeedsHuman_TransitionsToNeedsHuman()
    {
        var run = await CreateRunAsync(advanceTo: RunLifecycleState.AwaitingResult);
        var now = DateTimeOffset.UtcNow;

        var evt = new RuntimeEvent
        {
            EventId = "evt_nh_1",
            RunId = run.RunId,
            EventType = RuntimeEventTypes.NeedsHuman,
            OccurredAt = now,
            Message = "Ambiguous acceptance criteria",
        };

        await _service.IngestRuntimeEventAsync(evt, CancellationToken.None);

        var updated = await _runStore.GetByIdAsync(run.RunId, CancellationToken.None);
        Assert.Equal(RunLifecycleState.NeedsHuman, updated!.Status);
        Assert.Equal(now, updated.FinishedAt);
    }

    // ── IngestRuntimeEventAsync — runtime.cancelled ────────────────

    [Fact]
    public async Task IngestCancelled_TransitionsToCancelled()
    {
        var run = await CreateRunAsync(advanceTo: RunLifecycleState.AwaitingResult);
        var now = DateTimeOffset.UtcNow;

        var evt = new RuntimeEvent
        {
            EventId = "evt_cancel_1",
            RunId = run.RunId,
            EventType = RuntimeEventTypes.Cancelled,
            OccurredAt = now,
        };

        await _service.IngestRuntimeEventAsync(evt, CancellationToken.None);

        var updated = await _runStore.GetByIdAsync(run.RunId, CancellationToken.None);
        Assert.Equal(RunLifecycleState.Cancelled, updated!.Status);
        Assert.Equal(now, updated.FinishedAt);
    }

    // ── IngestRuntimeEventAsync — unsupported event type ───────────

    [Fact]
    public async Task IngestRuntimeEvent_RejectsUnsupportedEventType()
    {
        var run = await CreateRunAsync(advanceTo: RunLifecycleState.AwaitingResult);

        var evt = new RuntimeEvent
        {
            EventId = "evt_bad_type",
            RunId = run.RunId,
            EventType = "runtime.bogus_event",
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.IngestRuntimeEventAsync(evt, CancellationToken.None));

        Assert.Contains("Unsupported runtime event type", ex.Message);
        Assert.Contains("runtime.bogus_event", ex.Message);
    }

    // ── IngestRuntimeEventAsync — unsupported severity ───────────

    [Fact]
    public async Task IngestRuntimeEvent_RejectsUnsupportedSeverity()
    {
        var run = await CreateRunAsync(advanceTo: RunLifecycleState.AwaitingResult);

        var evt = new RuntimeEvent
        {
            EventId = "evt_bad_sev",
            RunId = run.RunId,
            EventType = RuntimeEventTypes.Status,
            Severity = (EventSeverity)99, // out-of-range enum value
            Message = "Event with invalid severity",
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.IngestRuntimeEventAsync(evt, CancellationToken.None));

        Assert.Contains("Unsupported severity", ex.Message);
        Assert.Contains("99", ex.Message);

        // Verify no lifecycle event was persisted for an invalid event
        var events = await _eventStore.ListByRunIdAsync(run.RunId, CancellationToken.None);
        Assert.DoesNotContain(events, e => e.EventId == "evt_bad_sev");
    }

    // ── IngestRuntimeEventAsync — branch_created / pr_created (informational) ──

    [Fact]
    public async Task IngestBranchCreated_RecordsFieldsWithoutStateChange()
    {
        var run = await CreateRunAsync(advanceTo: RunLifecycleState.AwaitingResult);

        var evt = new RuntimeEvent
        {
            EventId = "evt_bc_1",
            RunId = run.RunId,
            EventType = RuntimeEventTypes.BranchCreated,
            Payload = new Dictionary<string, object?> { ["branchName"] = "agent/123-fix" },
        };

        await _service.IngestRuntimeEventAsync(evt, CancellationToken.None);

        var updated = await _runStore.GetByIdAsync(run.RunId, CancellationToken.None);
        Assert.Equal(RunLifecycleState.AwaitingResult, updated!.Status);
        Assert.Equal("agent/123-fix", updated.BranchName);
    }

    [Fact]
    public async Task IngestPrCreated_RecordsFieldsWithoutStateChange()
    {
        var run = await CreateRunAsync(advanceTo: RunLifecycleState.AwaitingResult);

        var evt = new RuntimeEvent
        {
            EventId = "evt_pr_1",
            RunId = run.RunId,
            EventType = RuntimeEventTypes.PrCreated,
            Payload = new Dictionary<string, object?>
            {
                ["pullRequestUrl"] = "https://dev.azure.com/pr/999",
                ["branchName"] = "agent/999-feat",
            },
        };

        await _service.IngestRuntimeEventAsync(evt, CancellationToken.None);

        var updated = await _runStore.GetByIdAsync(run.RunId, CancellationToken.None);
        Assert.Equal(RunLifecycleState.AwaitingResult, updated!.Status);
        Assert.Equal("https://dev.azure.com/pr/999", updated.PullRequestUrl);
        Assert.Equal("agent/999-feat", updated.BranchName);
    }

    // ── AppendControllerEvent ──────────────────────────────────────

    [Fact]
    public async Task AppendControllerEvent_AddsControllerPrefixWhenMissing()
    {
        var run = await CreateRunAsync();

        await _service.AppendControllerEventAsync(
            run.RunId, "custom_event", "A custom controller event", null, CancellationToken.None);

        var events = await _eventStore.ListByRunIdAsync(run.RunId, CancellationToken.None);
        Assert.Contains(events, e => e.EventType == "controller.custom_event");
    }

    [Fact]
    public async Task AppendControllerEvent_KeepsExistingPrefix()
    {
        var run = await CreateRunAsync();

        await _service.AppendControllerEventAsync(
            run.RunId, "controller.already_prefixed", "Already prefixed", null, CancellationToken.None);

        var events = await _eventStore.ListByRunIdAsync(run.RunId, CancellationToken.None);
        Assert.Contains(events, e => e.EventType == "controller.already_prefixed");
    }

    // ── FindStaleRuns ──────────────────────────────────────────────

    [Fact]
    public async Task FindStaleRuns_ReturnsRunsWithStaleHeartbeat()
    {
        var run = await CreateRunAsync(advanceTo: RunLifecycleState.AwaitingResult);

        // Advance the "clock" by updating LastHeartbeatAt to be in the distant past
        await _runStore.UpdateRuntimeFieldsAsync(run.RunId, new RuntimeFieldUpdate
        {
            StartedAt = DateTimeOffset.UtcNow.AddHours(-2),
            LastHeartbeatAt = DateTimeOffset.UtcNow.AddHours(-1),
        }, CancellationToken.None);

        var stale = await _service.FindStaleRunsAsync(TimeSpan.FromMinutes(30), CancellationToken.None);
        Assert.Single(stale);
        Assert.Equal(run.RunId, stale[0].RunId);
    }

    [Fact]
    public async Task FindStaleRuns_ReturnsRunsWithoutAnyHeartbeat()
    {
        var run = await CreateRunAsync(advanceTo: RunLifecycleState.AwaitingResult);

        // Set StartedAt to be in the distant past but no heartbeat
        await _runStore.UpdateRuntimeFieldsAsync(run.RunId, new RuntimeFieldUpdate
        {
            StartedAt = DateTimeOffset.UtcNow.AddHours(-2),
        }, CancellationToken.None);

        var stale = await _service.FindStaleRunsAsync(TimeSpan.FromMinutes(30), CancellationToken.None);
        Assert.Single(stale);
    }

    [Fact]
    public async Task FindStaleRuns_ExcludesRunsWithRecentHeartbeat()
    {
        var run = await CreateRunAsync(advanceTo: RunLifecycleState.AwaitingResult);

        // Recent heartbeat
        await _runStore.UpdateRuntimeFieldsAsync(run.RunId, new RuntimeFieldUpdate
        {
            LastHeartbeatAt = DateTimeOffset.UtcNow.AddMinutes(-5),
        }, CancellationToken.None);

        var stale = await _service.FindStaleRunsAsync(TimeSpan.FromMinutes(30), CancellationToken.None);
        Assert.Empty(stale);
    }

    // ── RecoverStaleRun ────────────────────────────────────────────

    [Fact]
    public async Task RecoverStaleRun_TransitionsToNeedsHuman()
    {
        var run = await CreateRunAsync(advanceTo: RunLifecycleState.AwaitingResult);

        await _service.RecoverStaleRunAsync(run.RunId, CancellationToken.None);

        var updated = await _runStore.GetByIdAsync(run.RunId, CancellationToken.None);
        Assert.Equal(RunLifecycleState.NeedsHuman, updated!.Status);

        // Verify a warning-level stale_recovered event was appended
        var events = await _eventStore.ListByRunIdAsync(run.RunId, CancellationToken.None);
        var staleEvent = events.FirstOrDefault(e => e.EventType == "controller.stale_recovered");
        Assert.NotNull(staleEvent);
        Assert.Equal(EventSeverity.Warning, staleEvent.Severity);
    }

    [Fact]
    public async Task RecoverStaleRun_RejectsNonRecoverableState()
    {
        var run = await CreateRunAsync(); // Claimed, not AwaitingResult or AgentRunning

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.RecoverStaleRunAsync(run.RunId, CancellationToken.None));

        Assert.Contains("only runs in AwaitingResult or AgentRunning", ex.Message);
    }

    [Fact]
    public async Task RecoverStaleRun_RejectsAlreadyTerminalRun()
    {
        var run = await CreateRunAsync();
        await _runStore.ForceUpdateStatusAsync(run.RunId, RunLifecycleState.Failed);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.RecoverStaleRunAsync(run.RunId, CancellationToken.None));

        Assert.Contains("terminal state", ex.Message);
    }

    [Fact]
    public async Task RecoverStaleRun_AcceptsAgentRunningState()
    {
        // A pi-materia run that dies before emitting runtime.accepted
        // stays in AgentRunning forever — stale recovery must handle it.
        var run = await CreateRunAsync(advanceTo: RunLifecycleState.AgentRunning);

        await _service.RecoverStaleRunAsync(run.RunId, CancellationToken.None);

        var updated = await _runStore.GetByIdAsync(run.RunId, CancellationToken.None);
        Assert.Equal(RunLifecycleState.NeedsHuman, updated!.Status);

        // Verify stale_recovered event documents AgentRunning as previous state
        var events = await _eventStore.ListByRunIdAsync(run.RunId, CancellationToken.None);
        var staleEvent = events.FirstOrDefault(e => e.EventType == "controller.stale_recovered");
        Assert.NotNull(staleEvent);
        Assert.Equal(EventSeverity.Warning, staleEvent.Severity);
        Assert.Contains("AgentRunning", staleEvent.Message);
    }

    [Fact]
    public async Task RecoverStaleRunWithRetry_AcceptsAgentRunningState()
    {
        // StaleTimeout for AgentRunning goes straight to NeedsHuman (non-retryable).
        var run = await CreateRunAsync(advanceTo: RunLifecycleState.AgentRunning);

        var retryRun = await _service.RecoverStaleRunWithRetryAsync(
            run.RunId, "worker-1", 3, CancellationToken.None);

        Assert.Null(retryRun);

        var updated = await _runStore.GetByIdAsync(run.RunId, CancellationToken.None);
        Assert.Equal(RunLifecycleState.NeedsHuman, updated!.Status);

        // Verify stale_recovered event was recorded with AgentRunning as previous state
        var events = await _eventStore.ListByRunIdAsync(run.RunId, CancellationToken.None);
        var staleEvent = events.FirstOrDefault(e => e.EventType == "controller.stale_recovered");
        Assert.NotNull(staleEvent);
        Assert.Contains("AgentRunning", staleEvent.Message);
    }

    // ── Lifecycle projection — activeState / completedState ────────

    [Fact]
    public async Task Projection_UseActiveStateOnClaim()
    {
        // Verify that claiming an Azure-sourced work item projects ActiveState
        // to the external work source.
        var stubWorkSource = new StubWorkSource();
        var service = new RunLifecycleService(
            NullLogger<RunLifecycleService>.Instance, _runStore, _eventStore, _workItemStore, stubWorkSource, _workSourceOptions);

        var candidate = new WorkCandidate
        {
            Id = "wi_proj_active",
            ExternalId = "100",
            Source = "AzureDevOpsBoards",
            Title = "Projection active state test",
            Status = "New",
            RepoKey = "test-repo",
            Tags = new[] { "agent-ready" },
            ExternalUrl = "https://dev.azure.com/org/project/_workitems/edit/100",
            SourceMetadata = new Dictionary<string, string> { ["revision"] = "1" },
        };
        await _workItemStore.UpsertAsync(candidate, CancellationToken.None);

        var run = await service.CreateRunForWorkItemAsync(
            "wi_proj_active", "worker-1", CancellationToken.None);

        // The Claimed transition should project ActiveState ("Active")
        var statusUpdates = stubWorkSource.StatusUpdates;
        Assert.Single(statusUpdates);
        Assert.Equal("Active", statusUpdates[0].Status.Status);
        Assert.Contains("agent-active", statusUpdates[0].Status.Tags!);
    }

    [Fact]
    public async Task Projection_UseActiveStateOnAgentRunning()
    {
        var stubWorkSource = new StubWorkSource();
        var service = new RunLifecycleService(
            NullLogger<RunLifecycleService>.Instance, _runStore, _eventStore, _workItemStore, stubWorkSource, _workSourceOptions);

        var candidate = new WorkCandidate
        {
            Id = "wi_proj_running",
            ExternalId = "101",
            Source = "AzureDevOpsBoards",
            Title = "Projection running state test",
            Status = "New",
            RepoKey = "test-repo",
            Tags = new[] { "agent-ready" },
            ExternalUrl = "https://dev.azure.com/org/project/_workitems/edit/101",
            SourceMetadata = new Dictionary<string, string> { ["revision"] = "1" },
        };
        await _workItemStore.UpsertAsync(candidate, CancellationToken.None);

        var run = await service.CreateRunForWorkItemAsync(
            "wi_proj_running", "worker-1", CancellationToken.None);

        // Advance through states to AgentRunning
        await AdvanceToAsync(service, run.RunId, RunLifecycleState.AgentRunning, CancellationToken.None);

        // Should have status updates for Claimed and AgentRunning, both using ActiveState
        var statusUpdates = stubWorkSource.StatusUpdates;
        Assert.All(statusUpdates, su => Assert.Equal("Active", su.Status.Status));
    }

    [Fact]
    public async Task Projection_UseCompletedStateOnCompletion()
    {
        var stubWorkSource = new StubWorkSource();
        var service = new RunLifecycleService(
            NullLogger<RunLifecycleService>.Instance, _runStore, _eventStore, _workItemStore, stubWorkSource, _workSourceOptions);

        var candidate = new WorkCandidate
        {
            Id = "wi_proj_completed",
            ExternalId = "102",
            Source = "AzureDevOpsBoards",
            Title = "Projection completed state test",
            Status = "Active",
            RepoKey = "test-repo",
            Tags = new[] { "agent-ready" },
            ExternalUrl = "https://dev.azure.com/org/project/_workitems/edit/102",
            SourceMetadata = new Dictionary<string, string> { ["revision"] = "1" },
        };
        await _workItemStore.UpsertAsync(candidate, CancellationToken.None);

        var run = await service.CreateRunForWorkItemAsync(
            "wi_proj_completed", "worker-1", CancellationToken.None);

        // Advance to AwaitingResult then ingest a completed event
        await AdvanceToAsync(service, run.RunId, RunLifecycleState.AwaitingResult, CancellationToken.None);

        await service.IngestRuntimeEventAsync(new RuntimeEvent
        {
            EventId = "evt_proj_comp",
            RunId = run.RunId,
            EventType = RuntimeEventTypes.Completed,
            OccurredAt = DateTimeOffset.UtcNow,
            Message = "Done",
            Payload = new Dictionary<string, object?> { ["outcome"] = CompletionOutcomes.PullRequestOpened },
        }, CancellationToken.None);

        // The PrOpened transition (from completion) should project CompletedState ("Resolved")
        var statusUpdates = stubWorkSource.StatusUpdates;
        Assert.Contains(statusUpdates, su => su.Status.Status == "Resolved");
    }

    [Fact]
    public async Task Projection_FailedDoesNotChangeBoardState()
    {
        // Failed runs should add tags but NOT change the board state,
        // keeping the item visible on the active board.
        var stubWorkSource = new StubWorkSource();
        var service = new RunLifecycleService(
            NullLogger<RunLifecycleService>.Instance, _runStore, _eventStore, _workItemStore, stubWorkSource, _workSourceOptions);

        var candidate = new WorkCandidate
        {
            Id = "wi_proj_failed",
            ExternalId = "103",
            Source = "AzureDevOpsBoards",
            Title = "Projection failed state test",
            Status = "Active",
            RepoKey = "test-repo",
            Tags = new[] { "agent-ready" },
            ExternalUrl = "https://dev.azure.com/org/project/_workitems/edit/103",
            SourceMetadata = new Dictionary<string, string> { ["revision"] = "1" },
        };
        await _workItemStore.UpsertAsync(candidate, CancellationToken.None);

        var run = await service.CreateRunForWorkItemAsync(
            "wi_proj_failed", "worker-1", CancellationToken.None);

        await AdvanceToAsync(service, run.RunId, RunLifecycleState.AwaitingResult, CancellationToken.None);

        await service.IngestRuntimeEventAsync(new RuntimeEvent
        {
            EventId = "evt_proj_fail",
            RunId = run.RunId,
            EventType = RuntimeEventTypes.Failed,
            OccurredAt = DateTimeOffset.UtcNow,
            Severity = EventSeverity.Error,
            Message = "Something broke",
        }, CancellationToken.None);

        // Failed state should NOT add agent-failed tag (a bad runtime environment
        // should not dirty the external record). The projection returns null,
        // meaning no status update is sent for the Failed transition.
        // Earlier transitions (Claimed, AgentRunning, AwaitingResult) do send
        // status updates with Active state, but Failed itself sends none.
        var statusUpdatesWithTags = stubWorkSource.StatusUpdates
            .Where(su => su.Status?.Tags is { Count: > 0 })
            .ToList();

        // None of the status updates should carry an agent-failed tag
        Assert.DoesNotContain(
            statusUpdatesWithTags,
            su => su.Status.Tags!.Any(t => t.Contains("agent-failed", StringComparison.OrdinalIgnoreCase)));

        // A comment should have been added for the failure
        var failedComments = stubWorkSource.Comments
            .Where(c => c.Comment.Contains("failed", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.Single(failedComments);
    }

    [Fact]
    public async Task Projection_NeedsHumanDoesNotChangeBoardState()
    {
        var stubWorkSource = new StubWorkSource();
        var service = new RunLifecycleService(
            NullLogger<RunLifecycleService>.Instance, _runStore, _eventStore, _workItemStore, stubWorkSource, _workSourceOptions);

        var candidate = new WorkCandidate
        {
            Id = "wi_proj_needs_human",
            ExternalId = "104",
            Source = "AzureDevOpsBoards",
            Title = "Projection needs-human test",
            Status = "Active",
            RepoKey = "test-repo",
            Tags = new[] { "agent-ready" },
            ExternalUrl = "https://dev.azure.com/org/project/_workitems/edit/104",
            SourceMetadata = new Dictionary<string, string> { ["revision"] = "1" },
        };
        await _workItemStore.UpsertAsync(candidate, CancellationToken.None);

        var run = await service.CreateRunForWorkItemAsync(
            "wi_proj_needs_human", "worker-1", CancellationToken.None);

        await AdvanceToAsync(service, run.RunId, RunLifecycleState.AwaitingResult, CancellationToken.None);

        await service.IngestRuntimeEventAsync(new RuntimeEvent
        {
            EventId = "evt_proj_nh",
            RunId = run.RunId,
            EventType = RuntimeEventTypes.NeedsHuman,
            OccurredAt = DateTimeOffset.UtcNow,
            Message = "Need clarification",
        }, CancellationToken.None);

        var needsHumanUpdate = stubWorkSource.StatusUpdates
            .Where(su => su.Status.Tags?.Contains("agent-needs-human") == true)
            .ToList();

        Assert.Single(needsHumanUpdate);
        Assert.Null(needsHumanUpdate[0].Status.Status);
    }

    [Fact]
    public async Task Projection_IdempotentReProjection()
    {
        // Re-projecting the same state should not cause errors.
        // BuildExternalProjection sets the same ActiveState on Claimed and AgentRunning.
        // The ADO client handles PATCH with same value as idempotent.
        var stubWorkSource = new StubWorkSource();
        var service = new RunLifecycleService(
            NullLogger<RunLifecycleService>.Instance, _runStore, _eventStore, _workItemStore, stubWorkSource, _workSourceOptions);

        var candidate = new WorkCandidate
        {
            Id = "wi_proj_idem",
            ExternalId = "105",
            Source = "AzureDevOpsBoards",
            Title = "Idempotent projection test",
            Status = "New",
            RepoKey = "test-repo",
            Tags = new[] { "agent-ready" },
            ExternalUrl = "https://dev.azure.com/org/project/_workitems/edit/105",
            SourceMetadata = new Dictionary<string, string> { ["revision"] = "1" },
        };
        await _workItemStore.UpsertAsync(candidate, CancellationToken.None);

        var run = await service.CreateRunForWorkItemAsync(
            "wi_proj_idem", "worker-1", CancellationToken.None);

        // Advance through multiple states that all project ActiveState
        await AdvanceToAsync(service, run.RunId, RunLifecycleState.AwaitingResult, CancellationToken.None);

        // All status updates for active states should have the same ActiveState value.
        // This proves idempotent re-projection: same state PATCHed multiple times.
        var activeStateUpdates = stubWorkSource.StatusUpdates
            .Where(su => su.Status.Status == "Active")
            .ToList();

        // Claimed, AgentRunning, and AwaitingResult all project ActiveState
        Assert.Equal(3, activeStateUpdates.Count);
    }

    [Fact]
    public async Task Projection_NoActiveState_SkipsStateChange()
    {
        // When ActiveState is not configured, no board state change should occur.
        var noActiveOptions = new TestOptionsMonitor<WorkSourceOptionsView>(new WorkSourceOptionsView
        {
            // ActiveState intentionally null
            CompletedState = "Resolved",
        });

        var stubWorkSource = new StubWorkSource();
        var service = new RunLifecycleService(
            NullLogger<RunLifecycleService>.Instance, _runStore, _eventStore, _workItemStore, stubWorkSource, noActiveOptions);

        var candidate = new WorkCandidate
        {
            Id = "wi_proj_no_active",
            ExternalId = "106",
            Source = "AzureDevOpsBoards",
            Title = "No active state test",
            Status = "New",
            RepoKey = "test-repo",
            Tags = new[] { "agent-ready" },
            ExternalUrl = "https://dev.azure.com/org/project/_workitems/edit/106",
            SourceMetadata = new Dictionary<string, string> { ["revision"] = "1" },
        };
        await _workItemStore.UpsertAsync(candidate, CancellationToken.None);

        var run = await service.CreateRunForWorkItemAsync(
            "wi_proj_no_active", "worker-1", CancellationToken.None);

        // Claimed should project tags but NOT state when ActiveState is null
        var statusUpdates = stubWorkSource.StatusUpdates;
        Assert.Single(statusUpdates);
        Assert.Null(statusUpdates[0].Status.Status);
        Assert.Contains("agent-active", statusUpdates[0].Status.Tags!);
    }

    // ── Run-level retry tests ──────────────────────────────────────

    [Fact]
    public async Task EvaluateRetry_RetryableFailure_CreatesRetryRun()
    {
        var stubWorkSource = new StubWorkSource();
        var service = new RunLifecycleService(
            NullLogger<RunLifecycleService>.Instance, _runStore, _eventStore, _workItemStore, stubWorkSource, _workSourceOptions);

        // Create a work item and run
        var wi = await _workItemStore.CreateAsync(
            new CreateWorkItemRequest { RepoKey = "repo", Title = "Retry Test" }, CancellationToken.None);
        var run = await service.CreateRunForWorkItemAsync(wi.Id, "worker-1", CancellationToken.None);

        // Advance to AwaitingResult then fail with a retryable error
        await AdvanceToAsync(service, run.RunId, RunLifecycleState.AwaitingResult, CancellationToken.None);

        // Simulate a keepalive-stall failure (retryable)
        await _runStore.ForceUpdateStatusAsync(run.RunId, RunLifecycleState.Failed);
        await _runStore.UpdateRuntimeFieldsAsync(run.RunId, new RuntimeFieldUpdate
        {
            Error = "Keepalive-stall detected: keepalive_stall — no runtime event for 120s",
        }, CancellationToken.None);

        // Evaluate retry with max 3 attempts
        var retryRun = await service.EvaluateRetryAsync(run.RunId, "worker-1", 3, CancellationToken.None);

        // Should create a retry run
        Assert.NotNull(retryRun);
        Assert.Equal(2, retryRun.RunAttempt);
        Assert.Equal(run.RunId, retryRun.PreviousRunId);
        Assert.Equal(wi.Id, retryRun.WorkItemId);
        Assert.Equal(RunLifecycleState.Claimed, retryRun.Status);

        // Verify lifecycle events
        var events = await _eventStore.ListByRunIdAsync(retryRun.RunId, CancellationToken.None);
        Assert.Contains(events, e => e.EventType == "controller.retry_run_created");
    }

    [Fact]
    public async Task EvaluateRetry_NonRetryableFailure_ReturnsNull()
    {
        var stubWorkSource = new StubWorkSource();
        var service = new RunLifecycleService(
            NullLogger<RunLifecycleService>.Instance, _runStore, _eventStore, _workItemStore, stubWorkSource, _workSourceOptions);

        var wi = await _workItemStore.CreateAsync(
            new CreateWorkItemRequest { RepoKey = "repo", Title = "Non-Retry Test" }, CancellationToken.None);
        var run = await service.CreateRunForWorkItemAsync(wi.Id, "worker-1", CancellationToken.None);

        // Fail with a non-retryable error (no known retryable reason in error message)
        await _runStore.ForceUpdateStatusAsync(run.RunId, RunLifecycleState.Failed);
        await _runStore.UpdateRuntimeFieldsAsync(run.RunId, new RuntimeFieldUpdate
        {
            Error = "Tests failed after implementation",
        }, CancellationToken.None);

        // Evaluate retry — should return null (non-retryable)
        var retryRun = await service.EvaluateRetryAsync(run.RunId, "worker-1", 3, CancellationToken.None);
        Assert.Null(retryRun);
    }

    [Fact]
    public async Task EvaluateRetry_MaxAttemptsExceeded_EscalatesToNeedsHuman()
    {
        var stubWorkSource = new StubWorkSource();
        var service = new RunLifecycleService(
            NullLogger<RunLifecycleService>.Instance, _runStore, _eventStore, _workItemStore, stubWorkSource, _workSourceOptions);

        var wi = await _workItemStore.CreateAsync(
            new CreateWorkItemRequest { RepoKey = "repo", Title = "Exhaust Test" }, CancellationToken.None);

        // Create 3 failed runs (attempts 1, 2, 3)
        var runs = new List<AgentRunHandle>();
        for (int i = 1; i <= 3; i++)
        {
            var run = await service.CreateRunForWorkItemAsync(wi.Id, "worker-1", CancellationToken.None);
            await _runStore.ForceUpdateStatusAsync(run.RunId, RunLifecycleState.Failed);
            await _runStore.UpdateRuntimeFieldsAsync(run.RunId, new RuntimeFieldUpdate
            {
                Error = $"Attempt {i}: keepalive_stall failure",
                RunAttempt = i,
                PreviousRunId = i > 1 ? runs[^1].RunId : null,
            }, CancellationToken.None);
            runs.Add(run);
        }

        // Evaluate retry on the 3rd (last) attempt with max 3 — should escalate
        var retryRun = await service.EvaluateRetryAsync(runs[^1].RunId, "worker-1", 3, CancellationToken.None);
        Assert.Null(retryRun);

        // Verify escalation event was recorded
        var events = await _eventStore.ListByRunIdAsync(runs[^1].RunId, CancellationToken.None);
        Assert.Contains(events, e => e.EventType == "controller.retry_exhausted");
    }

    [Fact]
    public async Task EvaluateRetry_ProcessExitNonZero_IsRetryable()
    {
        var stubWorkSource = new StubWorkSource();
        var service = new RunLifecycleService(
            NullLogger<RunLifecycleService>.Instance, _runStore, _eventStore, _workItemStore, stubWorkSource, _workSourceOptions);

        var wi = await _workItemStore.CreateAsync(
            new CreateWorkItemRequest { RepoKey = "repo", Title = "Process Exit Test" }, CancellationToken.None);
        var run = await service.CreateRunForWorkItemAsync(wi.Id, "worker-1", CancellationToken.None);

        // Simulate process_exit_nonzero failure (retryable)
        await _runStore.ForceUpdateStatusAsync(run.RunId, RunLifecycleState.Failed);
        await _runStore.UpdateRuntimeFieldsAsync(run.RunId, new RuntimeFieldUpdate
        {
            Error = "pi exited with code 137 without a final event. process_exit_nonzero",
        }, CancellationToken.None);

        var retryRun = await service.EvaluateRetryAsync(run.RunId, "worker-1", 3, CancellationToken.None);
        Assert.NotNull(retryRun);
        Assert.Equal(2, retryRun.RunAttempt);
    }

    [Fact]
    public async Task RecoverStaleRunWithRetry_StaleTimeout_GoesToNeedsHuman()
    {
        // StaleTimeout is a non-retryable failure — goes straight to NeedsHuman.
        var wi = await _workItemStore.CreateAsync(
            new CreateWorkItemRequest { RepoKey = "repo", Title = "Stale Test" }, CancellationToken.None);
        var run = await CreateRunAsync(advanceTo: RunLifecycleState.AwaitingResult);

        // Recover stale run — should go to NeedsHuman, not retry
        var retryRun = await _service.RecoverStaleRunWithRetryAsync(
            run.RunId, "worker-1", 3, CancellationToken.None);

        Assert.Null(retryRun);

        var updated = await _runStore.GetByIdAsync(run.RunId, CancellationToken.None);
        Assert.Equal(RunLifecycleState.NeedsHuman, updated!.Status);

        // Verify stale_recovered event was recorded
        var events = await _eventStore.ListByRunIdAsync(run.RunId, CancellationToken.None);
        Assert.Contains(events, e => e.EventType == "controller.stale_recovered");
    }

    [Fact]
    public async Task RecoverStaleRunWithRetry_RejectsTerminalRun()
    {
        var run = await CreateRunAsync();
        await _runStore.ForceUpdateStatusAsync(run.RunId, RunLifecycleState.Failed);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.RecoverStaleRunWithRetryAsync(run.RunId, "worker-1", 3, CancellationToken.None));

        Assert.Contains("terminal state", ex.Message);
    }

    [Fact]
    public async Task RecoverStaleRunWithRetry_RejectsNonRecoverableState()
    {
        var run = await CreateRunAsync(); // Claimed, not AwaitingResult or AgentRunning

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.RecoverStaleRunWithRetryAsync(run.RunId, "worker-1", 3, CancellationToken.None));

        Assert.Contains("only runs in AwaitingResult or AgentRunning", ex.Message);
    }

    [Fact]
    public async Task IngestFailedRetryable_TransitionsToFailed()
    {
        var run = await CreateRunAsync(advanceTo: RunLifecycleState.AwaitingResult);
        var now = DateTimeOffset.UtcNow;

        var evt = new RuntimeEvent
        {
            EventId = "evt_fail_retryable_1",
            RunId = run.RunId,
            EventType = RuntimeEventTypes.FailedRetryable,
            OccurredAt = now,
            Severity = EventSeverity.Error,
            Message = "Keepalive-stall detected: no runtime event for 120s",
            Payload = new Dictionary<string, object?>
            {
                ["reason"] = "keepalive_stall",
                ["summary"] = "Stall detected",
                ["retryable"] = true,
            },
        };

        await _service.IngestRuntimeEventAsync(evt, CancellationToken.None);

        var updated = await _runStore.GetByIdAsync(run.RunId, CancellationToken.None);
        Assert.Equal(RunLifecycleState.Failed, updated!.Status);
        Assert.Contains("Keepalive-stall", updated.Error);
        Assert.Equal(now, updated.FinishedAt);

        // Verify retryable_failure event was recorded
        var events = await _eventStore.ListByRunIdAsync(run.RunId, CancellationToken.None);
        Assert.Contains(events, e => e.EventType == "controller.retryable_failure");
    }

    [Fact]
    public async Task RetryChain_TracksPreviousRunId()
    {
        var stubWorkSource = new StubWorkSource();
        var service = new RunLifecycleService(
            NullLogger<RunLifecycleService>.Instance, _runStore, _eventStore, _workItemStore, stubWorkSource, _workSourceOptions);

        var wi = await _workItemStore.CreateAsync(
            new CreateWorkItemRequest { RepoKey = "repo", Title = "Chain Test" }, CancellationToken.None);

        // Create initial run (attempt 1)
        var run1 = await service.CreateRunForWorkItemAsync(wi.Id, "worker-1", CancellationToken.None);
        Assert.Equal(1, run1.RunAttempt);
        Assert.Null(run1.PreviousRunId);

        // Fail run 1 with retryable error
        await _runStore.ForceUpdateStatusAsync(run1.RunId, RunLifecycleState.Failed);
        await _runStore.UpdateRuntimeFieldsAsync(run1.RunId, new RuntimeFieldUpdate
        {
            Error = "keepalive_stall failure",
        }, CancellationToken.None);

        // Evaluate retry → creates run 2
        var run2 = await service.EvaluateRetryAsync(run1.RunId, "worker-1", 3, CancellationToken.None);
        Assert.NotNull(run2);
        Assert.Equal(2, run2.RunAttempt);
        Assert.Equal(run1.RunId, run2.PreviousRunId);

        // Fail run 2 with retryable error
        await _runStore.ForceUpdateStatusAsync(run2.RunId, RunLifecycleState.Failed);
        await _runStore.UpdateRuntimeFieldsAsync(run2.RunId, new RuntimeFieldUpdate
        {
            Error = "process_exit_nonzero failure",
        }, CancellationToken.None);

        // Evaluate retry → creates run 3
        var run3 = await service.EvaluateRetryAsync(run2.RunId, "worker-1", 3, CancellationToken.None);
        Assert.NotNull(run3);
        Assert.Equal(3, run3.RunAttempt);
        Assert.Equal(run2.RunId, run3.PreviousRunId);

        // Fail run 3 — should exhaust retries
        await _runStore.ForceUpdateStatusAsync(run3.RunId, RunLifecycleState.Failed);
        await _runStore.UpdateRuntimeFieldsAsync(run3.RunId, new RuntimeFieldUpdate
        {
            Error = "keepalive_stall failure",
        }, CancellationToken.None);

        var run4 = await service.EvaluateRetryAsync(run3.RunId, "worker-1", 3, CancellationToken.None);
        Assert.Null(run4); // Exhausted
    }

    // ── Helpers ────────────────────────────────────────────────────

    /// <summary>
    /// Creates a run in Claimed state and optionally advances it to a target state.
    /// </summary>
    private async Task<AgentRunHandle> CreateRunAsync(RunLifecycleState advanceTo = RunLifecycleState.Claimed)
    {
        var wi = await _workItemStore.CreateAsync(
            new CreateWorkItemRequest { RepoKey = "repo", Title = "Test Work Item" }, CancellationToken.None);

        var run = await _service.CreateRunForWorkItemAsync(wi.Id, "worker-1", CancellationToken.None);

        if (advanceTo == RunLifecycleState.Claimed)
            return run;

        // Advance through controller-owned states up to the target
        var orderedStates = new[]
        {
            RunLifecycleState.EnvironmentProvisioning,
            RunLifecycleState.EnvironmentReady,
            RunLifecycleState.RepositoryCloning,
            RunLifecycleState.RepositoryReady,
            RunLifecycleState.ContextInjected,
            RunLifecycleState.AgentStarting,
            RunLifecycleState.AgentRunning,
            RunLifecycleState.AwaitingResult,
        };

        foreach (var state in orderedStates)
        {
            if ((int)state > (int)advanceTo)
                break;

            await _service.TransitionAsync(run.RunId, state, CancellationToken.None);
        }

        return (await _runStore.GetByIdAsync(run.RunId, CancellationToken.None))!;
    }

    /// <summary>
    /// Creates a minimal runtime event for testing.
    /// </summary>
    private static RuntimeEvent CreateEvent(
        string runId,
        string eventId,
        string eventType,
        string? message = null,
        string? runtimeRunId = null)
    {
        return new RuntimeEvent
        {
            RunId = runId,
            EventId = eventId,
            EventType = eventType,
            OccurredAt = DateTimeOffset.UtcNow,
            Message = message,
            RuntimeRunId = runtimeRunId,
        };
    }

    /// <summary>
    /// Advance a run through controller-owned states up to the target state.
    /// Used by lifecycle projection tests that construct their own service instances.
    /// </summary>
    private static async Task AdvanceToAsync(
        RunLifecycleService service,
        string runId,
        RunLifecycleState targetState,
        CancellationToken ct)
    {
        var orderedStates = new[]
        {
            RunLifecycleState.EnvironmentProvisioning,
            RunLifecycleState.EnvironmentReady,
            RunLifecycleState.RepositoryCloning,
            RunLifecycleState.RepositoryReady,
            RunLifecycleState.ContextInjected,
            RunLifecycleState.AgentStarting,
            RunLifecycleState.AgentRunning,
            RunLifecycleState.AwaitingResult,
        };

        foreach (var state in orderedStates)
        {
            if ((int)state > (int)targetState)
                break;

            await service.TransitionAsync(runId, state, ct);
        }
    }

    // ── In-memory store implementations for testing ────────────────

    private sealed class InMemoryAgentRunStore : IAgentRunStore
    {
        private readonly Dictionary<string, AgentRunHandle> _runs = new();

        public Task<AgentRunHandle> CreateAsync(CreateRunRequest request, CancellationToken ct)
        {
            var run = new AgentRunHandle
            {
                RunId = $"run_{Guid.NewGuid():N}",
                WorkItemId = request.WorkItemId,
                Status = request.InitialStatus,
                RunAttempt = request.RunAttempt,
                PreviousRunId = request.PreviousRunId,
                StartedAt = request.InitialStatus > RunLifecycleState.Claimed ? DateTimeOffset.UtcNow : null,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            _runs[run.RunId] = run;
            return Task.FromResult(run);
        }

        public Task<AgentRunHandle?> GetByIdAsync(string runId, CancellationToken ct)
        {
            _runs.TryGetValue(runId, out var run);
            return Task.FromResult(run);
        }

        public Task UpdateStatusAsync(string runId, RunLifecycleState status, CancellationToken ct)
        {
            if (_runs.TryGetValue(runId, out var run))
            {
                var updated = run with
                {
                    Status = status,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    StartedAt = run.StartedAt ?? (status > RunLifecycleState.Claimed ? DateTimeOffset.UtcNow : null),
                    FinishedAt = run.FinishedAt ?? (IsTerminal(status) ? DateTimeOffset.UtcNow : null),
                };
                _runs[runId] = updated;
            }
            return Task.CompletedTask;
        }

        public Task UpdateRuntimeFieldsAsync(string runId, RuntimeFieldUpdate update, CancellationToken ct)
        {
            if (_runs.TryGetValue(runId, out var run))
            {
                _runs[runId] = run with
                {
                    RuntimeRunId = update.RuntimeRunId ?? run.RuntimeRunId,
                    RuntimeType = update.RuntimeType ?? run.RuntimeType,
                    EnvironmentId = update.EnvironmentId ?? run.EnvironmentId,
                    BranchName = update.BranchName ?? run.BranchName,
                    PullRequestUrl = update.PullRequestUrl ?? run.PullRequestUrl,
                    ResultSummary = update.ResultSummary ?? run.ResultSummary,
                    StartedAt = update.StartedAt ?? run.StartedAt,
                    FinishedAt = update.FinishedAt ?? run.FinishedAt,
                    LastHeartbeatAt = update.LastHeartbeatAt ?? run.LastHeartbeatAt,
                    Error = update.Error ?? run.Error,
                    RunAttempt = update.RunAttempt ?? run.RunAttempt,
                    PreviousRunId = update.PreviousRunId ?? run.PreviousRunId,
                    UpdatedAt = DateTimeOffset.UtcNow,
                };
            }
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AgentRunHandle>> ListAsync(ListRunsQuery query, CancellationToken ct)
        {
            var results = _runs.Values.AsEnumerable();
            if (query.Status.HasValue)
                results = results.Where(r => r.Status == query.Status.Value);
            if (!string.IsNullOrWhiteSpace(query.WorkItemId))
                results = results.Where(r => r.WorkItemId == query.WorkItemId);
            return Task.FromResult<IReadOnlyList<AgentRunHandle>>(results.OrderByDescending(r => r.CreatedAt).ToList());
        }

        public Task<IReadOnlyList<AgentRunHandle>> FindStaleAsync(TimeSpan staleTimeout, CancellationToken ct)
        {
            var cutoff = DateTimeOffset.UtcNow - staleTimeout;
            var results = _runs.Values
                .Where(r => r.Status == RunLifecycleState.AwaitingResult
                         || r.Status == RunLifecycleState.AgentRunning)
                .Where(r => (r.LastHeartbeatAt == null && r.StartedAt < cutoff) || (r.LastHeartbeatAt < cutoff))
                .OrderBy(r => r.LastHeartbeatAt ?? r.StartedAt)
                .ToList();
            return Task.FromResult<IReadOnlyList<AgentRunHandle>>(results);
        }

        public Task<int> CountActiveAsync(CancellationToken ct)
        {
            return Task.FromResult(_runs.Values.Count(r =>
                r.Status != RunLifecycleState.Completed &&
                r.Status != RunLifecycleState.Failed &&
                r.Status != RunLifecycleState.Cancelled &&
                r.Status != RunLifecycleState.CleanedUp));
        }

        public Task<AgentRunHandle?> FindLatestRunByWorkItemAsync(string workItemId, CancellationToken ct)
        {
            var latest = _runs.Values
                .Where(r => r.WorkItemId == workItemId)
                .OrderByDescending(r => r.CreatedAt)
                .FirstOrDefault();
            return Task.FromResult<AgentRunHandle?>(latest);
        }

        /// <summary>
        /// Test helper: force a status update without going through TransitionAsync validation.
        /// </summary>
        public Task ForceUpdateStatusAsync(string runId, RunLifecycleState status)
        {
            if (_runs.TryGetValue(runId, out var run))
            {
                _runs[runId] = run with
                {
                    Status = status,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    FinishedAt = IsTerminal(status) ? DateTimeOffset.UtcNow : run.FinishedAt,
                };
            }
            return Task.CompletedTask;
        }

        private static bool IsTerminal(RunLifecycleState s) =>
            s is RunLifecycleState.Completed or RunLifecycleState.Failed
                or RunLifecycleState.Cancelled or RunLifecycleState.CleanedUp;
    }

    private sealed class InMemoryLifecycleEventStore : ILifecycleEventStore
    {
        private readonly List<LifecycleEvent> _events = new();

        public Task AppendAsync(LifecycleEvent evt, CancellationToken ct)
        {
            var id = evt.Id;
            if (string.IsNullOrWhiteSpace(id))
                id = $"evt_{Guid.NewGuid():N}";

            _events.Add(evt with { Id = id });
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<LifecycleEvent>> ListByRunIdAsync(string runId, CancellationToken ct)
        {
            return Task.FromResult<IReadOnlyList<LifecycleEvent>>(
                _events.Where(e => e.RunId == runId).OrderBy(e => e.CreatedAt).ToList());
        }

        public Task<bool> ExistsByEventIdAsync(string runId, string eventId, CancellationToken ct)
        {
            return Task.FromResult(_events.Any(e => e.RunId == runId && e.EventId == eventId));
        }
    }

    private sealed class InMemoryWorkItemStore : IWorkItemStore
    {
        private readonly Dictionary<string, WorkCandidate> _items = new();

        public Task<WorkCandidate> CreateAsync(CreateWorkItemRequest request, CancellationToken ct)
        {
            var item = new WorkCandidate
            {
                Id = $"wi_{Guid.NewGuid():N}",
                ExternalId = "local-fake",
                RepoKey = request.RepoKey,
                Title = request.Title,
                Description = request.Body ?? request.Description,
                AcceptanceCriteria = request.AcceptanceCriteria,
                Priority = request.Priority,
                Status = request.Status,
                Tags = request.Tags,
                Source = request.Source,
            };
            _items[item.Id] = item;
            return Task.FromResult(item);
        }

        public Task<IReadOnlyList<WorkCandidate>> ListAsync(ListWorkItemsQuery query, CancellationToken ct)
        {
            return Task.FromResult<IReadOnlyList<WorkCandidate>>(_items.Values.ToList());
        }

        public Task<WorkCandidate?> GetByIdAsync(string id, CancellationToken ct)
        {
            _items.TryGetValue(id, out var item);
            return Task.FromResult(item);
        }

        public Task<IReadOnlyList<WorkCandidate>> FindEligibleAsync(WorkQuery query, CancellationToken ct)
        {
            return Task.FromResult<IReadOnlyList<WorkCandidate>>(_items.Values.ToList());
        }

        public Task<ClaimResult> TryClaimAsync(string workItemId, ClaimRequest claim, CancellationToken ct)
        {
            if (!_items.ContainsKey(workItemId))
            {
                return Task.FromResult(new ClaimResult
                {
                    Success = false,
                    FailureReason = $"Work item '{workItemId}' not found.",
                });
            }
            return Task.FromResult(new ClaimResult { Success = true });
        }

        public Task UpdateStatusAsync(string workItemId, string status, CancellationToken ct)
        {
            if (_items.TryGetValue(workItemId, out var item))
            {
                _items[workItemId] = item with { Status = status };
            }
            return Task.CompletedTask;
        }

        public Task<WorkCandidate> UpsertAsync(WorkCandidate candidate, CancellationToken ct)
        {
            // Match by (Source, ExternalId)
            var existing = _items.Values.FirstOrDefault(
                i => i.Source == candidate.Source && i.ExternalId == candidate.ExternalId);

            if (existing is not null)
            {
                var updated = existing with
                {
                    Title = candidate.Title,
                    Description = candidate.Description,
                    RepoKey = candidate.RepoKey,
                    Priority = candidate.Priority,
                    Status = candidate.Status,
                    AssignedTo = candidate.AssignedTo,
                    ExternalUrl = candidate.ExternalUrl,
                    AcceptanceCriteria = candidate.AcceptanceCriteria,
                    Tags = candidate.Tags,
                    SourceMetadata = candidate.SourceMetadata,
                };
                _items[existing.Id] = updated;
                return Task.FromResult(updated);
            }

            // Insert new
            var item = new WorkCandidate
            {
                Id = candidate.Id.Length > 0 ? candidate.Id : $"wi_{Guid.NewGuid():N}",
                ExternalId = candidate.ExternalId,
                ExternalUrl = candidate.ExternalUrl,
                RepoKey = candidate.RepoKey,
                Title = candidate.Title,
                Description = candidate.Description,
                AcceptanceCriteria = candidate.AcceptanceCriteria,
                Priority = candidate.Priority,
                Status = candidate.Status,
                Tags = candidate.Tags,
                AssignedTo = candidate.AssignedTo,
                Source = candidate.Source,
                SourceMetadata = candidate.SourceMetadata,
            };
            _items[item.Id] = item;
            return Task.FromResult(item);
        }
    }

    /// <summary>
    /// Stub <see cref="IWorkSource"/> that records calls for assertion
    /// but performs no real external operations. Used by unit tests
    /// that do not need real Azure DevOps or local-fake work source behavior.
    /// </summary>
    private sealed class StubWorkSource : IWorkSource
    {
        public List<(ExternalWorkRef WorkRef, ExternalWorkStatus Status)> StatusUpdates { get; } = new();
        public List<(ExternalWorkRef WorkRef, string Comment)> Comments { get; } = new();

        public Task<IReadOnlyList<WorkCandidate>> FindEligibleAsync(
            WorkQuery query, CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<WorkCandidate>>([]);
        }

        public Task<ClaimResult> TryClaimAsync(
            WorkCandidate candidate, ClaimRequest claim, CancellationToken cancellationToken)
        {
            return Task.FromResult(new ClaimResult { Success = true });
        }

        public Task UpdateStatusAsync(
            ExternalWorkRef workRef, ExternalWorkStatus status, CancellationToken cancellationToken)
        {
            StatusUpdates.Add((workRef, status));
            return Task.CompletedTask;
        }

        public Task AddCommentAsync(
            ExternalWorkRef workRef, string comment, CancellationToken cancellationToken)
        {
            Comments.Add((workRef, comment));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<WorkItemComment>> GetCommentsAsync(
            ExternalWorkRef workRef, int maxComments, CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<WorkItemComment>>(Array.Empty<WorkItemComment>());
        }

        public Task ReleaseClaimAsync(
            ReleaseClaimRequest request, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Minimal <see cref="IOptionsMonitor{TOptions}"/> implementation for unit tests.
    /// Returns a fixed value and does not support change notifications.
    /// </summary>
    private sealed class TestOptionsMonitor<TOptions> : IOptionsMonitor<TOptions>
        where TOptions : class
    {
        private readonly TOptions _value;

        public TestOptionsMonitor(TOptions value)
        {
            _value = value ?? throw new ArgumentNullException(nameof(value));
        }

        public TOptions CurrentValue => _value;

        public TOptions Get(string? name) => _value;

        public IDisposable OnChange(Action<TOptions, string?> listener) =>
            Disposable.Create(() => { });

        private sealed class Disposable : IDisposable
        {
            private static readonly IDisposable Instance = new NoopDisposable();

            public static IDisposable Create(Action? action = null) => Instance;

            public void Dispose() { }

            private sealed class NoopDisposable : IDisposable
            {
                public void Dispose() { }
            }
        }
    }
}

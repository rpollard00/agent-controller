using AgentController.Application;
using AgentController.Application.Services;
using AgentController.Domain;

namespace AgentController.Application.Tests;

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

    public RunLifecycleServiceTests()
    {
        _runStore = new InMemoryAgentRunStore();
        _eventStore = new InMemoryLifecycleEventStore();
        _workItemStore = new InMemoryWorkItemStore();
        _service = new RunLifecycleService(_runStore, _eventStore, _workItemStore, new StubWorkSource());
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
            _runStore, _eventStore, _workItemStore, stubWorkSource);

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
    public async Task RecoverStaleRun_RejectsNonAwaitingResultState()
    {
        var run = await CreateRunAsync(); // Claimed, not AwaitingResult

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.RecoverStaleRunAsync(run.RunId, CancellationToken.None));

        Assert.Contains("only runs in AwaitingResult", ex.Message);
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
                .Where(r => r.Status == RunLifecycleState.AwaitingResult)
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
    }
}

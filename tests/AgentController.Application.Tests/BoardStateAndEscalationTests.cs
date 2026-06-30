using AgentController.Application;
using AgentController.Application.Abstractions;
using AgentController.Application.Services;
using AgentController.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentController.Application.Tests;

/// <summary>
/// Tests asserting board state driving and failure-escalation paths.
///
/// Covers:
/// (1) runtime.accepted triggers System.State=Active and claim-only Claimed does not
/// (2) cast completion drives System.State=Resolved once config is corrected
/// (3) pre-accept setup failure increments the persistent attempt counter and
///     escalates to agent-needs-human after MaxRunAttempts
/// (4) MaybeProjectToWorkSource failure emits a logged warning + lifecycle event
///     instead of being swallowed
/// (5) Retryable pre-accept failure reasons (environment_provisioning_failed,
///     repository_clone_failed) are classified correctly
/// </summary>
public class BoardStateAndEscalationTests
{
    private readonly InMemoryAgentRunStore _runStore;
    private readonly InMemoryLifecycleEventStore _eventStore;
    private readonly InMemoryWorkItemStore _workItemStore;
    private readonly RunLifecycleService _service;

    public BoardStateAndEscalationTests()
    {
        _runStore = new InMemoryAgentRunStore();
        _eventStore = new InMemoryLifecycleEventStore();
        _workItemStore = new InMemoryWorkItemStore();

        var workSourceOptions = new TestOptionsMonitor<WorkSourceOptionsView>(new WorkSourceOptionsView
        {
            ActiveState = "Active",
            CompletedState = "Resolved",
        });

        _service = new RunLifecycleService(
            NullLogger<RunLifecycleService>.Instance,
            _runStore, _eventStore, _workItemStore,
            new StubWorkSource(), workSourceOptions);
    }

    // ═══════════════════════════════════════════════════════════════
    // (1) runtime.accepted triggers System.State=Active,
    //     claim-only Claimed does NOT change board state
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task RuntimeAccepted_ProjectsActiveStateToWorkSource()
    {
        // Arrange: create a service with a tracking StubWorkSource
        var stubWorkSource = new StubWorkSource();
        var service = new RunLifecycleService(
            NullLogger<RunLifecycleService>.Instance,
            _runStore, _eventStore, _workItemStore,
            stubWorkSource,
            new TestOptionsMonitor<WorkSourceOptionsView>(new WorkSourceOptionsView
            {
                ActiveState = "Active",
                CompletedState = "Resolved",
            }));

        var candidate = CreateAzureCandidate("wi_accepted_proj", "200");
        await _workItemStore.UpsertAsync(candidate, CancellationToken.None);

        // Act: create run (Claimed — should project tags only, no status)
        var run = await service.CreateRunForWorkItemAsync(
            "wi_accepted_proj", "worker-1", CancellationToken.None);

        // Advance through controller states to AgentStarting (before runtime.accepted)
        await AdvanceToAsync(service, run.RunId, RunLifecycleState.AgentStarting, CancellationToken.None);

        // At this point, the Claimed projection should have tags only, no Status
        var claimedUpdates = stubWorkSource.StatusUpdates
            .Where(su => su.Status.Tags?.Contains("agent-active") == true)
            .ToList();
        Assert.NotEmpty(claimedUpdates);
        // The Claimed projection should NOT have a Status value
        Assert.All(claimedUpdates, su => Assert.Null(su.Status.Status));

        // Advance to AwaitingResult
        await AdvanceToAsync(service, run.RunId, RunLifecycleState.AwaitingResult, CancellationToken.None);

        // Now ingest runtime.accepted — this should project ActiveState
        var acceptedEvent = new RuntimeEvent
        {
            EventId = "evt_accepted_board",
            RunId = run.RunId,
            EventType = RuntimeEventTypes.Accepted,
            OccurredAt = DateTimeOffset.UtcNow,
            Message = "Runtime accepted",
            RuntimeRunId = "pi_runtime_123",
        };
        await service.IngestRuntimeEventAsync(acceptedEvent, CancellationToken.None);

        // Assert: there should be a status update with Status = "Active"
        var activeUpdates = stubWorkSource.StatusUpdates
            .Where(su => su.Status?.Status == "Active")
            .ToList();
        Assert.NotEmpty(activeUpdates);
    }

    [Fact]
    public async Task Claimed_DoesNotProjectBoardState()
    {
        // Verify that claiming an Azure-sourced work item does NOT change the board state.
        // Only tags (agent-active) are projected at Claim time.
        var stubWorkSource = new StubWorkSource();
        var service = new RunLifecycleService(
            NullLogger<RunLifecycleService>.Instance,
            _runStore, _eventStore, _workItemStore,
            stubWorkSource,
            new TestOptionsMonitor<WorkSourceOptionsView>(new WorkSourceOptionsView
            {
                ActiveState = "Active",
                CompletedState = "Resolved",
            }));

        var candidate = CreateAzureCandidate("wi_claimed_no_state", "210");
        await _workItemStore.UpsertAsync(candidate, CancellationToken.None);

        var run = await service.CreateRunForWorkItemAsync(
            "wi_claimed_no_state", "worker-1", CancellationToken.None);

        // Assert: the only status update so far is from Claimed, which has no Status
        var statusUpdates = stubWorkSource.StatusUpdates;
        Assert.Single(statusUpdates);
        Assert.Null(statusUpdates[0].Status.Status);
        Assert.Contains("agent-active", statusUpdates[0].Status.Tags!);

        // Assert: the local work item was NOT updated to "Active"
        var workItem = await _workItemStore.GetByIdAsync("wi_claimed_no_state", CancellationToken.None);
        Assert.NotNull(workItem);
        Assert.Equal("New", workItem!.Status); // still "New", not "Active"
    }

    // ═══════════════════════════════════════════════════════════════
    // (2) Cast completion drives System.State=Resolved
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Completion_ProjectsResolvedState()
    {
        var stubWorkSource = new StubWorkSource();
        var service = new RunLifecycleService(
            NullLogger<RunLifecycleService>.Instance,
            _runStore, _eventStore, _workItemStore,
            stubWorkSource,
            new TestOptionsMonitor<WorkSourceOptionsView>(new WorkSourceOptionsView
            {
                ActiveState = "Active",
                CompletedState = "Resolved",
            }));

        var candidate = CreateAzureCandidate("wi_completion", "201");
        await _workItemStore.UpsertAsync(candidate, CancellationToken.None);

        var run = await service.CreateRunForWorkItemAsync(
            "wi_completion", "worker-1", CancellationToken.None);
        await AdvanceToAsync(service, run.RunId, RunLifecycleState.AwaitingResult, CancellationToken.None);

        // Act: ingest a completed event with pull_request_opened outcome
        var completedEvent = new RuntimeEvent
        {
            EventId = "evt_completed_resolved",
            RunId = run.RunId,
            EventType = RuntimeEventTypes.Completed,
            OccurredAt = DateTimeOffset.UtcNow,
            Message = "Work complete",
            Payload = new Dictionary<string, object?>
            {
                ["outcome"] = CompletionOutcomes.PullRequestOpened,
                ["pullRequestUrl"] = "https://dev.azure.com/pr/456",
                ["summary"] = "Fixed the bug",
            },
        };
        await service.IngestRuntimeEventAsync(completedEvent, CancellationToken.None);

        // Assert: run should be in PrOpened state
        var updated = await _runStore.GetByIdAsync(run.RunId, CancellationToken.None);
        Assert.Equal(RunLifecycleState.PrOpened, updated!.Status);

        // Assert: a status update with "Resolved" was sent to the work source
        var resolvedUpdates = stubWorkSource.StatusUpdates
            .Where(su => su.Status?.Status == "Resolved")
            .ToList();
        Assert.NotEmpty(resolvedUpdates);

        // Assert: the local work item store was also updated to "Resolved"
        var workItem = await _workItemStore.GetByIdAsync("wi_completion", CancellationToken.None);
        Assert.NotNull(workItem);
        Assert.Equal("Resolved", workItem!.Status);
    }

    [Fact]
    public async Task Completion_BranchPushed_AlsoProjectsResolved()
    {
        var stubWorkSource = new StubWorkSource();
        var service = new RunLifecycleService(
            NullLogger<RunLifecycleService>.Instance,
            _runStore, _eventStore, _workItemStore,
            stubWorkSource,
            new TestOptionsMonitor<WorkSourceOptionsView>(new WorkSourceOptionsView
            {
                ActiveState = "Active",
                CompletedState = "Resolved",
            }));

        var candidate = CreateAzureCandidate("wi_branchpush", "202");
        await _workItemStore.UpsertAsync(candidate, CancellationToken.None);

        var run = await service.CreateRunForWorkItemAsync(
            "wi_branchpush", "worker-1", CancellationToken.None);
        await AdvanceToAsync(service, run.RunId, RunLifecycleState.AwaitingResult, CancellationToken.None);

        var completedEvent = new RuntimeEvent
        {
            EventId = "evt_completed_branch",
            RunId = run.RunId,
            EventType = RuntimeEventTypes.Completed,
            OccurredAt = DateTimeOffset.UtcNow,
            Payload = new Dictionary<string, object?>
            {
                ["outcome"] = CompletionOutcomes.BranchPushed,
                ["branchName"] = "agent/202-fix",
            },
        };
        await service.IngestRuntimeEventAsync(completedEvent, CancellationToken.None);

        var resolvedUpdates = stubWorkSource.StatusUpdates
            .Where(su => su.Status?.Status == "Resolved")
            .ToList();
        Assert.NotEmpty(resolvedUpdates);
    }

    // ═══════════════════════════════════════════════════════════════
    // (3) Pre-accept setup failure increments attempt counter and
    //     escalates to agent-needs-human after MaxRunAttempts
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task PreAcceptFailure_EnvironmentProvisioningFailed_IsRetryable()
    {
        var wi = await _workItemStore.CreateAsync(
            new CreateWorkItemRequest { RepoKey = "repo", Title = "Env Fail Test" },
            CancellationToken.None);

        var run = await _service.CreateRunForWorkItemAsync(wi.Id, "worker-1", CancellationToken.None);

        // Simulate environment provisioning failure (retryable pre-accept failure)
        await _runStore.ForceUpdateStatusAsync(run.RunId, RunLifecycleState.Failed);
        await _runStore.UpdateRuntimeFieldsAsync(run.RunId, new RuntimeFieldUpdate
        {
            Error = "[environment_provisioning_failed] Environment provisioning failed: timeout",
            FinishedAt = DateTimeOffset.UtcNow,
        }, CancellationToken.None);

        // Evaluate retry — should create a retry run because the error
        // contains the retryable reason "environment_provisioning_failed"
        var retryRun = await _service.EvaluateRetryAsync(
            run.RunId, "worker-1", 3, CancellationToken.None);

        Assert.NotNull(retryRun);
        Assert.Equal(2, retryRun.RunAttempt);
        Assert.Equal(run.RunId, retryRun.PreviousRunId);

        // Verify lifecycle event for retry creation
        var events = await _eventStore.ListByRunIdAsync(retryRun.RunId, CancellationToken.None);
        Assert.Contains(events, e => e.EventType == "controller.retry_run_created");
    }

    [Fact]
    public async Task PreAcceptFailure_RepositoryCloneFailed_IsRetryable()
    {
        var wi = await _workItemStore.CreateAsync(
            new CreateWorkItemRequest { RepoKey = "repo", Title = "Clone Fail Test" },
            CancellationToken.None);

        var run = await _service.CreateRunForWorkItemAsync(wi.Id, "worker-1", CancellationToken.None);

        // Simulate repository clone failure (retryable pre-accept failure)
        await _runStore.ForceUpdateStatusAsync(run.RunId, RunLifecycleState.Failed);
        await _runStore.UpdateRuntimeFieldsAsync(run.RunId, new RuntimeFieldUpdate
        {
            Error = "[repository_clone_failed] Repository clone failed: network timeout",
            FinishedAt = DateTimeOffset.UtcNow,
        }, CancellationToken.None);

        var retryRun = await _service.EvaluateRetryAsync(
            run.RunId, "worker-1", 3, CancellationToken.None);

        Assert.NotNull(retryRun);
        Assert.Equal(2, retryRun.RunAttempt);
        Assert.Equal(run.RunId, retryRun.PreviousRunId);
    }

    [Fact]
    public async Task PreAcceptFailure_ExhaustedRetries_EscalatesToNeedsHuman()
    {
        var stubWorkSource = new StubWorkSource();
        var service = new RunLifecycleService(
            NullLogger<RunLifecycleService>.Instance,
            _runStore, _eventStore, _workItemStore,
            stubWorkSource,
            new TestOptionsMonitor<WorkSourceOptionsView>(new WorkSourceOptionsView
            {
                ActiveState = "Active",
                CompletedState = "Resolved",
            }));

        // Use an Azure-sourced candidate so projection to external work source is triggered
        var candidate = CreateAzureCandidate("wi_exhaust", "400");
        await _workItemStore.UpsertAsync(candidate, CancellationToken.None);

        // Create 3 failed runs with retryable pre-accept errors
        var runs = new List<AgentRunHandle>();
        for (int i = 1; i <= 3; i++)
        {
            var run = await service.CreateRunForWorkItemAsync("wi_exhaust", "worker-1", CancellationToken.None);
            await _runStore.ForceUpdateStatusAsync(run.RunId, RunLifecycleState.Failed);
            await _runStore.UpdateRuntimeFieldsAsync(run.RunId, new RuntimeFieldUpdate
            {
                Error = $"[environment_provisioning_failed] Attempt {i} failed",
                RunAttempt = i,
                PreviousRunId = i > 1 ? runs[^1].RunId : null,
                FinishedAt = DateTimeOffset.UtcNow,
            }, CancellationToken.None);
            runs.Add(run);
        }

        // Evaluate retry on the 3rd attempt with max 3 — should escalate
        var retryRun = await service.EvaluateRetryAsync(
            runs[^1].RunId, "worker-1", 3, CancellationToken.None);
        Assert.Null(retryRun);

        // Verify escalation event was recorded
        var events = await _eventStore.ListByRunIdAsync(runs[^1].RunId, CancellationToken.None);
        Assert.Contains(events, e => e.EventType == "controller.retry_exhausted");

        // Verify the work item was updated to NeedsHuman
        var updatedWi = await _workItemStore.GetByIdAsync("wi_exhaust", CancellationToken.None);
        Assert.NotNull(updatedWi);
        Assert.Equal("NeedsHuman", updatedWi!.Status);

        // Verify agent-needs-human tag was projected to the external work source
        var needsHumanUpdates = stubWorkSource.StatusUpdates
            .Where(su => su.Status?.Tags?.Contains("agent-needs-human") == true)
            .ToList();
        Assert.NotEmpty(needsHumanUpdates);
    }

    [Fact]
    public async Task PreAcceptFailure_ChainTracksPreviousRunId()
    {
        var wi = await _workItemStore.CreateAsync(
            new CreateWorkItemRequest { RepoKey = "repo", Title = "Chain Test" },
            CancellationToken.None);

        // Run 1: initial, fails with environment_provisioning_failed
        var run1 = await _service.CreateRunForWorkItemAsync(wi.Id, "worker-1", CancellationToken.None);
        Assert.Equal(1, run1.RunAttempt);
        Assert.Null(run1.PreviousRunId);

        await _runStore.ForceUpdateStatusAsync(run1.RunId, RunLifecycleState.Failed);
        await _runStore.UpdateRuntimeFieldsAsync(run1.RunId, new RuntimeFieldUpdate
        {
            Error = "[environment_provisioning_failed] Attempt 1",
            FinishedAt = DateTimeOffset.UtcNow,
        }, CancellationToken.None);

        // Run 2: retry
        var run2 = await _service.EvaluateRetryAsync(run1.RunId, "worker-1", 3, CancellationToken.None);
        Assert.NotNull(run2);
        Assert.Equal(2, run2!.RunAttempt);
        Assert.Equal(run1.RunId, run2.PreviousRunId);

        await _runStore.ForceUpdateStatusAsync(run2.RunId, RunLifecycleState.Failed);
        await _runStore.UpdateRuntimeFieldsAsync(run2.RunId, new RuntimeFieldUpdate
        {
            Error = "[repository_clone_failed] Attempt 2",
            FinishedAt = DateTimeOffset.UtcNow,
        }, CancellationToken.None);

        // Run 3: retry
        var run3 = await _service.EvaluateRetryAsync(run2.RunId, "worker-1", 3, CancellationToken.None);
        Assert.NotNull(run3);
        Assert.Equal(3, run3!.RunAttempt);
        Assert.Equal(run2.RunId, run3.PreviousRunId);

        await _runStore.ForceUpdateStatusAsync(run3.RunId, RunLifecycleState.Failed);
        await _runStore.UpdateRuntimeFieldsAsync(run3.RunId, new RuntimeFieldUpdate
        {
            Error = "[environment_provisioning_failed] Attempt 3",
            FinishedAt = DateTimeOffset.UtcNow,
        }, CancellationToken.None);

        // Run 4: should exhaust retries
        var run4 = await _service.EvaluateRetryAsync(run3.RunId, "worker-1", 3, CancellationToken.None);
        Assert.Null(run4);
    }

    // ═══════════════════════════════════════════════════════════════
    // (4) MaybeProjectToWorkSource failure emits lifecycle event
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task WorkSourceProjectionFailure_EmitsLifecycleEvent()
    {
        // Arrange: use a StubWorkSource that throws on UpdateStatusAsync
        var failingWorkSource = new FailingWorkSource();
        var service = new RunLifecycleService(
            NullLogger<RunLifecycleService>.Instance,
            _runStore, _eventStore, _workItemStore,
            failingWorkSource,
            new TestOptionsMonitor<WorkSourceOptionsView>(new WorkSourceOptionsView
            {
                ActiveState = "Active",
                CompletedState = "Resolved",
            }));

        var candidate = CreateAzureCandidate("wi_proj_fail", "300");
        await _workItemStore.UpsertAsync(candidate, CancellationToken.None);

        // Act: create run — Claimed projection will fail via FailingWorkSource
        var run = await service.CreateRunForWorkItemAsync(
            "wi_proj_fail", "worker-1", CancellationToken.None);

        // Assert: the run was still created (projection failure is not fatal)
        Assert.NotNull(run);
        Assert.Equal(RunLifecycleState.Claimed, run.Status);

        // Assert: a lifecycle event was recorded for the projection failure
        var events = await _eventStore.ListByRunIdAsync(run.RunId, CancellationToken.None);
        var projectionFailureEvent = events
            .FirstOrDefault(e => e.EventType == ControllerEventTypes.WorkSourceProjectionFailed);
        Assert.NotNull(projectionFailureEvent);
        Assert.Equal(EventSeverity.Warning, projectionFailureEvent!.Severity);
        Assert.Contains(run.RunId, projectionFailureEvent.Message);
        Assert.Contains("300", projectionFailureEvent.Message);
        Assert.Contains("AzureDevOpsBoards", projectionFailureEvent.Message);
        Assert.Contains("Claimed", projectionFailureEvent.Message);

        // Assert: the event payload contains structured data
        Assert.NotNull(projectionFailureEvent.Payload);
        Assert.Equal(run.RunId, projectionFailureEvent.Payload!["runId"]);
        Assert.Equal("AzureDevOpsBoards", projectionFailureEvent.Payload!["source"]);
        Assert.Equal("Simulated work source failure", projectionFailureEvent.Payload!["error"]);
    }

    [Fact]
    public async Task WorkSourceProjectionFailure_DoesNotBlockTransition()
    {
        // Arrange
        var failingWorkSource = new FailingWorkSource();
        var service = new RunLifecycleService(
            NullLogger<RunLifecycleService>.Instance,
            _runStore, _eventStore, _workItemStore,
            failingWorkSource,
            new TestOptionsMonitor<WorkSourceOptionsView>(new WorkSourceOptionsView
            {
                ActiveState = "Active",
                CompletedState = "Resolved",
            }));

        var candidate = CreateAzureCandidate("wi_proj_fail2", "301");
        await _workItemStore.UpsertAsync(candidate, CancellationToken.None);

        var run = await service.CreateRunForWorkItemAsync(
            "wi_proj_fail2", "worker-1", CancellationToken.None);

        // Act: transition through states — all projections will fail
        // but transitions should still succeed
        await service.TransitionAsync(run.RunId, RunLifecycleState.EnvironmentProvisioning, CancellationToken.None);
        await service.TransitionAsync(run.RunId, RunLifecycleState.EnvironmentReady, CancellationToken.None);
        await service.TransitionAsync(run.RunId, RunLifecycleState.RepositoryCloning, CancellationToken.None);

        // Assert: run is in RepositoryCloning (transitions succeeded despite projection failures)
        var updated = await _runStore.GetByIdAsync(run.RunId, CancellationToken.None);
        Assert.Equal(RunLifecycleState.RepositoryCloning, updated!.Status);

        // Assert: multiple projection failure events were recorded
        var events = await _eventStore.ListByRunIdAsync(run.RunId, CancellationToken.None);
        var projectionFailures = events
            .Where(e => e.EventType == ControllerEventTypes.WorkSourceProjectionFailed)
            .ToList();
        Assert.NotEmpty(projectionFailures);
    }

    // ═══════════════════════════════════════════════════════════════
    // (5) Retryable failure reason classification
    // ═══════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(RetryableFailureReasons.EnvironmentProvisioningFailed)]
    [InlineData(RetryableFailureReasons.RepositoryCloneFailed)]
    [InlineData(RetryableFailureReasons.KeepaliveStall)]
    [InlineData(RetryableFailureReasons.ProcessExitNonZero)]
    [InlineData(RetryableFailureReasons.ProcessStartFailed)]
    [InlineData(RetryableFailureReasons.EnvironmentUnreachable)]
    public void RetryableReasons_AllKnownReasons_AreRetryable(string reason)
    {
        Assert.True(RetryableFailureReasons.IsRetryable(reason, null));
        Assert.Contains(reason, RetryableFailureReasons.AllRetryableReasons);
    }

    [Theory]
    [InlineData("tests_failed")]
    [InlineData("ambiguous_requirements")]
    [InlineData("compile_error")]
    [InlineData(null)]
    [InlineData("")]
    public void NonRetryableReasons_NotRetryable(string? reason)
    {
        Assert.False(RetryableFailureReasons.IsRetryable(reason, null));
    }

    [Fact]
    public void RetryablePayloadFlag_OverridesReasonCheck()
    {
        var payload = new Dictionary<string, object?> { ["retryable"] = true };
        Assert.True(RetryableFailureReasons.IsRetryable("unknown_reason", payload));

        var notRetryable = new Dictionary<string, object?> { ["retryable"] = false };
        Assert.False(RetryableFailureReasons.IsRetryable(RetryableFailureReasons.KeepaliveStall, notRetryable));
    }

    // ═══════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════

    private static WorkCandidate CreateAzureCandidate(string id, string externalId)
    {
        return new WorkCandidate
        {
            Id = id,
            ExternalId = externalId,
            Source = "AzureDevOpsBoards",
            Title = $"Test work item {externalId}",
            Status = "New",
            RepoKey = "test-repo",
            Tags = new[] { "agent-ready" },
            ExternalUrl = $"https://dev.azure.com/org/project/_workitems/edit/{externalId}",
            SourceMetadata = new Dictionary<string, string> { ["revision"] = "1" },
        };
    }

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

            try
            {
                await service.TransitionAsync(runId, state, ct);
            }
            catch (InvalidOperationException)
            {
                // Run is already past this state (idempotent advance)
            }
        }
    }

    // ── Test infrastructure ────────────────────────────────────────

    private sealed class NullLogger<T> : ILogger<T>, ILogger
    {
        public static NullLogger<T> Instance { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null!;
        public bool IsEnabled(LogLevel level) => false;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
    }

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
                _runs[runId] = run with
                {
                    Status = status,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    StartedAt = run.StartedAt ?? (status > RunLifecycleState.Claimed ? DateTimeOffset.UtcNow : null),
                    FinishedAt = run.FinishedAt ?? (IsTerminal(status) ? DateTimeOffset.UtcNow : null),
                };
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
            return Task.FromResult<IReadOnlyList<AgentRunHandle>>(
                results.OrderByDescending(r => r.CreatedAt).ToList());
        }

        public Task<IReadOnlyList<AgentRunHandle>> FindStaleAsync(TimeSpan staleTimeout, CancellationToken ct)
        {
            var cutoff = DateTimeOffset.UtcNow - staleTimeout;
            return Task.FromResult<IReadOnlyList<AgentRunHandle>>(_runs.Values
                .Where(r => r.Status == RunLifecycleState.AwaitingResult
                         || r.Status == RunLifecycleState.AgentRunning)
                .Where(r => (r.LastHeartbeatAt == null && r.StartedAt < cutoff)
                         || (r.LastHeartbeatAt < cutoff))
                .OrderBy(r => r.LastHeartbeatAt ?? r.StartedAt)
                .ToList());
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
                return Task.FromResult(new ClaimResult { Success = false, FailureReason = "Not found" });
            return Task.FromResult(new ClaimResult { Success = true });
        }

        public Task UpdateStatusAsync(string workItemId, string status, CancellationToken ct)
        {
            if (_items.TryGetValue(workItemId, out var item))
                _items[workItemId] = item with { Status = status };
            return Task.CompletedTask;
        }

        public Task<WorkCandidate> UpsertAsync(WorkCandidate candidate, CancellationToken ct)
        {
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
    /// Stub IWorkSource that records calls for assertion.
    /// </summary>
    private sealed class StubWorkSource : IWorkSource
    {
        public List<(ExternalWorkRef WorkRef, ExternalWorkStatus Status)> StatusUpdates { get; } = new();
        public List<(ExternalWorkRef WorkRef, string Comment)> Comments { get; } = new();

        public Task<IReadOnlyList<WorkCandidate>> FindEligibleAsync(WorkQuery query, CancellationToken ct)
        {
            return Task.FromResult<IReadOnlyList<WorkCandidate>>([]);
        }

        public Task<ClaimResult> TryClaimAsync(WorkCandidate candidate, ClaimRequest claim, CancellationToken ct)
        {
            return Task.FromResult(new ClaimResult { Success = true });
        }

        public Task UpdateStatusAsync(ExternalWorkRef workRef, ExternalWorkStatus status, CancellationToken ct)
        {
            StatusUpdates.Add((workRef, status));
            return Task.CompletedTask;
        }

        public Task AddCommentAsync(ExternalWorkRef workRef, string comment, CancellationToken ct)
        {
            Comments.Add((workRef, comment));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<WorkItemComment>> GetCommentsAsync(ExternalWorkRef workRef, int maxComments, CancellationToken ct)
        {
            return Task.FromResult<IReadOnlyList<WorkItemComment>>(Array.Empty<WorkItemComment>());
        }

        public Task ReleaseClaimAsync(ReleaseClaimRequest request, CancellationToken ct)
        {
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Stub IWorkSource that throws on UpdateStatusAsync to simulate projection failures.
    /// </summary>
    private sealed class FailingWorkSource : IWorkSource
    {
        public Task<IReadOnlyList<WorkCandidate>> FindEligibleAsync(WorkQuery query, CancellationToken ct)
        {
            return Task.FromResult<IReadOnlyList<WorkCandidate>>([]);
        }

        public Task<ClaimResult> TryClaimAsync(WorkCandidate candidate, ClaimRequest claim, CancellationToken ct)
        {
            return Task.FromResult(new ClaimResult { Success = true });
        }

        public Task UpdateStatusAsync(ExternalWorkRef workRef, ExternalWorkStatus status, CancellationToken ct)
        {
            throw new InvalidOperationException("Simulated work source failure");
        }

        public Task AddCommentAsync(ExternalWorkRef workRef, string comment, CancellationToken ct)
        {
            throw new InvalidOperationException("Simulated work source failure");
        }

        public Task<IReadOnlyList<WorkItemComment>> GetCommentsAsync(ExternalWorkRef workRef, int maxComments, CancellationToken ct)
        {
            return Task.FromResult<IReadOnlyList<WorkItemComment>>(Array.Empty<WorkItemComment>());
        }

        public Task ReleaseClaimAsync(ReleaseClaimRequest request, CancellationToken ct)
        {
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Minimal IOptionsMonitor for unit tests.
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
        public IDisposable OnChange(Action<TOptions, string?> listener) => Disposable.Instance;

        private sealed class Disposable : IDisposable
        {
            public static readonly Disposable Instance = new();
            public void Dispose() { }
        }
    }
}
using AgentController.Application;
using AgentController.Application.Services;
using AgentController.Domain;
using AgentController.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentController.Infrastructure.Tests;

/// <summary>
/// Integration-style tests for <see cref="MockPiMateriaRuntime"/>, verifying that:
/// <list type="bullet">
///   <item>The runtime emits the expected sequence of events (accepted → heartbeat → status → completed).</item>
///   <item>Events emitted by the mock runtime drive a run from AwaitingResult to Completed.</item>
///   <item>The runtime reports its assigned ID via the returned handle.</item>
///   <item>The runtime handles already-terminal runs gracefully.</item>
/// </list>
///
/// Uses a minimal DI container with in-memory persistence stores and the real
/// <see cref="RunLifecycleService"/> to validate end-to-end runtime → controller interaction.
/// </summary>
public class MockPiMateriaRuntimeTests : IAsyncLifetime
{
    private ServiceProvider _provider = null!;
    private IServiceScopeFactory _scopeFactory = null!;

    public Task InitializeAsync()
    {
        var services = new ServiceCollection();

        // In-memory stores (singletons for simplicity — they hold state)
        services.AddSingleton<InMemoryAgentRunStore>();
        services.AddSingleton<InMemoryLifecycleEventStore>();
        services.AddSingleton<InMemoryWorkItemStore>();
        services.AddSingleton<IAgentRunStore>(sp => sp.GetRequiredService<InMemoryAgentRunStore>());
        services.AddSingleton<ILifecycleEventStore>(sp => sp.GetRequiredService<InMemoryLifecycleEventStore>());
        services.AddSingleton<IWorkItemStore>(sp => sp.GetRequiredService<InMemoryWorkItemStore>());

        // Stub work source
        services.AddSingleton<IWorkSource, StubWorkSource>();

        // Logging
        services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning));

        // RunLifecycleService (scoped, but we use scope factory)
        services.AddScoped<IRunLifecycleService, RunLifecycleService>();

        // Mock runtime (singleton, uses IServiceScopeFactory internally)
        services.AddSingleton<IAgentRuntime, MockPiMateriaRuntime>();

        _provider = services.BuildServiceProvider();
        _scopeFactory = _provider.GetRequiredService<IServiceScopeFactory>();

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_provider is not null)
        {
            await _provider.DisposeAsync();
        }
    }

    // ── Smoke tests ──────────────────────────────────────────────────

    [Fact]
    public void MockPiMateriaRuntime_ImplementsInterface()
    {
        var type = typeof(MockPiMateriaRuntime);
        Assert.True(
            typeof(IAgentRuntime).IsAssignableFrom(type),
            "MockPiMateriaRuntime should implement IAgentRuntime");
    }

    [Fact]
    public void AddAgentControllerMockPiMateriaRuntime_RegistersSingleton()
    {
        var services = new ServiceCollection();

        services.AddSingleton<IServiceScopeFactory>(sp =>
        {
            // Minimal scope factory for DI resolution (not used in smoke test)
            throw new NotImplementedException("Scope factory not needed for smoke test");
        });
        services.AddLogging();

        services.AddAgentControllerMockPiMateriaRuntime();
        var provider = services.BuildServiceProvider();

        var runtime1 = provider.GetRequiredService<IAgentRuntime>();
        var runtime2 = provider.GetRequiredService<IAgentRuntime>();

        Assert.Same(runtime1, runtime2);
        Assert.IsType<MockPiMateriaRuntime>(runtime1);
    }

    // ── End-to-end: mock runtime drives run to completion ───────────

    [Fact]
    public async Task StartAsync_BackgroundEvents_DriveToCompleted()
    {
        // ── Arrange: create work item and advance run to AgentStarting ──
        var runtime = _provider.GetRequiredService<IAgentRuntime>();
        string runId;

        await using (var setupScope = _scopeFactory.CreateAsyncScope())
        {
            var lifecycle = setupScope.ServiceProvider.GetRequiredService<IRunLifecycleService>();
            var workItemStore = setupScope.ServiceProvider.GetRequiredService<IWorkItemStore>();

            var wi = await workItemStore.CreateAsync(
                new CreateWorkItemRequest { RepoKey = "test-repo", Title = "E2E Test" },
                CancellationToken.None);

            var run = await lifecycle.CreateRunForWorkItemAsync(wi.Id, "test-worker", CancellationToken.None);
            runId = run.RunId;

            // Advance through controller-owned states up to (and including) AgentStarting
            await lifecycle.TransitionAsync(runId, RunLifecycleState.EnvironmentProvisioning, CancellationToken.None);
            await lifecycle.TransitionAsync(runId, RunLifecycleState.EnvironmentReady, CancellationToken.None);
            await lifecycle.TransitionAsync(runId, RunLifecycleState.RepositoryCloning, CancellationToken.None);
            await lifecycle.TransitionAsync(runId, RunLifecycleState.RepositoryReady, CancellationToken.None);
            await lifecycle.TransitionAsync(runId, RunLifecycleState.ContextInjected, CancellationToken.None);
            await lifecycle.TransitionAsync(runId, RunLifecycleState.AgentStarting, CancellationToken.None);
        }

        // ── Act: start the mock runtime ──
        var spec = new AgentRunSpec
        {
            RunId = runId,
            WorkRef = new ExternalWorkRef { Source = "LocalFile", ExternalId = "test-1" },
            RuntimeProfile = "success-pr",
        };

        var handle = await runtime.StartAsync(spec, CancellationToken.None);

        // ── Assert: handle has expected properties ──
        Assert.Equal(runId, handle.RunId);
        Assert.NotNull(handle.RuntimeRunId);
        Assert.StartsWith("mock-pi-", handle.RuntimeRunId, StringComparison.Ordinal);

        // ── Wait for background events to be emitted and processed ──
        // The mock runtime emits 4 events with 50ms delays, so a few seconds is generous.
        // The default "success-pr" loadout maps to PrOpened state.
        var runStore = _provider.GetRequiredService<InMemoryAgentRunStore>();
        for (int i = 0; i < 60; i++)
        {
            var r = await runStore.GetByIdAsync(runId, CancellationToken.None);
            if (r is not null && r.Status >= RunLifecycleState.PrOpened)
            {
                // Run reached PrOpened or beyond
                Assert.Equal(RunLifecycleState.PrOpened, r.Status);
                Assert.NotNull(r.PullRequestUrl);
                Assert.Contains("mock-", r.PullRequestUrl, StringComparison.Ordinal);
                Assert.NotNull(r.ResultSummary);
                Assert.NotNull(r.FinishedAt);
                break;
            }
            await Task.Delay(100);
        }

        // ── Verify final state ──
        var finalRun = await runStore.GetByIdAsync(runId, CancellationToken.None);

        Assert.NotNull(finalRun);
        Assert.Equal(RunLifecycleState.PrOpened, finalRun!.Status);
        Assert.NotNull(finalRun.PullRequestUrl);
        Assert.Contains("mock-", finalRun.PullRequestUrl, StringComparison.Ordinal);
        Assert.NotNull(finalRun.ResultSummary);
        Assert.NotNull(finalRun.FinishedAt);

        // ── Verify lifecycle events ──
        var eventStore = _provider.GetRequiredService<InMemoryLifecycleEventStore>();
        var events = await eventStore.ListByRunIdAsync(runId, CancellationToken.None);

        // Should have controller events + runtime events
        Assert.Contains(events, e => e.EventType == RuntimeEventTypes.Accepted);
        Assert.Contains(events, e => e.EventType == RuntimeEventTypes.Heartbeat);
        Assert.Contains(events, e => e.EventType == RuntimeEventTypes.Status);
        Assert.Contains(events, e => e.EventType == RuntimeEventTypes.Completed);
        Assert.Contains(events, e => e.EventType == "controller.runtime_event_ingested");
    }

    // ── Idempotency: already-completed run ───────────────────────────

    [Fact]
    public async Task StartAsync_AlreadyTerminalRun_DoesNotCrash()
    {
        var runtime = _provider.GetRequiredService<IAgentRuntime>();
        var runStore = _provider.GetRequiredService<InMemoryAgentRunStore>();

        // Create a run that is already in terminal state
        string runId;
        await using (var setupScope = _scopeFactory.CreateAsyncScope())
        {
            var lifecycle = setupScope.ServiceProvider.GetRequiredService<IRunLifecycleService>();
            var workItemStore = setupScope.ServiceProvider.GetRequiredService<IWorkItemStore>();

            var wi = await workItemStore.CreateAsync(
                new CreateWorkItemRequest { RepoKey = "repo", Title = "Terminal Test" },
                CancellationToken.None);

            var run = await lifecycle.CreateRunForWorkItemAsync(wi.Id, "w", CancellationToken.None);
            runId = run.RunId;

            // Advance to AwaitingResult then complete via runtime event
            await lifecycle.TransitionAsync(runId, RunLifecycleState.EnvironmentProvisioning, CancellationToken.None);
            await lifecycle.TransitionAsync(runId, RunLifecycleState.EnvironmentReady, CancellationToken.None);
            await lifecycle.TransitionAsync(runId, RunLifecycleState.RepositoryCloning, CancellationToken.None);
            await lifecycle.TransitionAsync(runId, RunLifecycleState.RepositoryReady, CancellationToken.None);
            await lifecycle.TransitionAsync(runId, RunLifecycleState.ContextInjected, CancellationToken.None);
            await lifecycle.TransitionAsync(runId, RunLifecycleState.AgentStarting, CancellationToken.None);
            await lifecycle.TransitionAsync(runId, RunLifecycleState.AgentRunning, CancellationToken.None);
            await lifecycle.TransitionAsync(runId, RunLifecycleState.AwaitingResult, CancellationToken.None);

            await lifecycle.IngestRuntimeEventAsync(
                new RuntimeEvent
                {
                    EventId = "pre-complete",
                    RunId = runId,
                    EventType = RuntimeEventTypes.Completed,
                    Payload = new Dictionary<string, object?> { ["outcome"] = CompletionOutcomes.NoChangesNeeded },
                },
                CancellationToken.None);
        }

        // ── Act: try to start mock runtime on already-completed run ──
        var spec = new AgentRunSpec
        {
            RunId = runId,
            WorkRef = new ExternalWorkRef { Source = "LocalFile", ExternalId = "terminal-1" },
        };

        // This should not throw — the mock runtime handles rejected events gracefully.
        var handle = await runtime.StartAsync(spec, CancellationToken.None);
        Assert.NotNull(handle);

        // Give the background task time to attempt emission (should be gracefully rejected)
        await Task.Delay(300);

        // Run state should still be Completed
        var finalRun = await runStore.GetByIdAsync(runId, CancellationToken.None);
        Assert.NotNull(finalRun);
        Assert.Equal(RunLifecycleState.Completed, finalRun!.Status);
    }

    // ── GetStatusAsync and CancelAsync ───────────────────────────────

    [Fact]
    public async Task GetStatusAsync_ReturnsCurrentStatus()
    {
        var runtime = _provider.GetRequiredService<IAgentRuntime>();

        var handle = new AgentRunHandle
        {
            RunId = "test-get-status",
            RuntimeRunId = "pi-123",
            Status = RunLifecycleState.AwaitingResult,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            LastHeartbeatAt = DateTimeOffset.UtcNow.AddSeconds(-30),
        };

        var status = await runtime.GetStatusAsync(handle, CancellationToken.None);

        Assert.Equal(RunLifecycleState.AwaitingResult, status.Status);
        Assert.Equal("pi-123", status.RuntimeRunId);
        Assert.NotNull(status.StartedAt);
        Assert.NotNull(status.LastHeartbeatAt);
    }

    [Fact]
    public async Task CancelAsync_DoesNotThrow()
    {
        var runtime = _provider.GetRequiredService<IAgentRuntime>();

        var handle = new AgentRunHandle { RunId = "test-cancel" };
        await runtime.CancelAsync(handle, CancellationToken.None);

        // No exception means success
        Assert.True(true);
    }

    // ── Helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Poll until the run reaches or exceeds the target state predicate,
    /// or until the timeout is exceeded.
    /// </summary>
    private async Task WaitForRunStateAsync(
        string runId,
        Func<RunLifecycleState, bool> predicate,
        TimeSpan timeout)
    {
        var runStore = _provider.GetRequiredService<InMemoryAgentRunStore>();
        await WaitForStateInStoreAsync(runStore, runId, predicate, timeout);
    }

    private static async Task WaitForStateInStoreAsync(
        InMemoryAgentRunStore runStore,
        string runId,
        Func<RunLifecycleState, bool> predicate,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            var run = await runStore.GetByIdAsync(runId, CancellationToken.None);
            if (run is not null && predicate(run.Status))
                return;

            await Task.Delay(50);
        }

        // Timeout — fetch final state for diagnostic assertion
        var finalRun = await runStore.GetByIdAsync(runId, CancellationToken.None);
        var finalStatus = finalRun?.Status.ToString() ?? "not found";
        Assert.Fail($"Run '{runId}' did not reach target state within {timeout.TotalSeconds}s. Final state: {finalStatus}.");
    }

    // ── In-memory store implementations (minimal) ────────────────────

    private sealed class InMemoryAgentRunStore : IAgentRunStore
    {
        private readonly object _lock = new();
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
            lock (_lock) { _runs[run.RunId] = run; }
            return Task.FromResult(run);
        }

        public Task<AgentRunHandle?> GetByIdAsync(string runId, CancellationToken ct)
        {
            AgentRunHandle? run;
            lock (_lock) { _runs.TryGetValue(runId, out run); }
            return Task.FromResult(run);
        }

        public Task UpdateStatusAsync(string runId, RunLifecycleState status, CancellationToken ct)
        {
            lock (_lock)
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
            }
            return Task.CompletedTask;
        }

        public Task UpdateRuntimeFieldsAsync(string runId, RuntimeFieldUpdate update, CancellationToken ct)
        {
            lock (_lock)
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
            }
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AgentRunHandle>> ListAsync(ListRunsQuery query, CancellationToken ct)
        {
            lock (_lock)
            {
                var results = _runs.Values.AsEnumerable();
                if (query.Status.HasValue)
                    results = results.Where(r => r.Status == query.Status.Value);
                return Task.FromResult<IReadOnlyList<AgentRunHandle>>(results.ToList());
            }
        }

        public Task<IReadOnlyList<AgentRunHandle>> FindStaleAsync(TimeSpan staleTimeout, CancellationToken ct)
        {
            return Task.FromResult<IReadOnlyList<AgentRunHandle>>([]);
        }

        public Task<int> CountActiveAsync(CancellationToken ct)
        {
            lock (_lock)
            {
                return Task.FromResult(_runs.Values.Count(r =>
                    r.Status != RunLifecycleState.Completed &&
                    r.Status != RunLifecycleState.Failed &&
                    r.Status != RunLifecycleState.Cancelled &&
                    r.Status != RunLifecycleState.CleanedUp));
            }
        }

        public Task<AgentRunHandle?> FindLatestRunByWorkItemAsync(string workItemId, CancellationToken ct)
        {
            lock (_lock)
            {
                var latest = _runs.Values
                    .Where(r => r.WorkItemId == workItemId)
                    .OrderByDescending(r => r.CreatedAt)
                    .FirstOrDefault();
                return Task.FromResult<AgentRunHandle?>(latest);
            }
        }

        public Task<IReadOnlyList<AgentRunHandle>> FindRunsForFeedbackAsync(CancellationToken ct)
        {
            lock (_lock)
            {
                var eligibleStatuses = new[]
                {
                    RunLifecycleState.PrOpened,
                    RunLifecycleState.BranchPushed,
                    RunLifecycleState.Completed,
                };
                var results = _runs.Values
                    .Where(r => eligibleStatuses.Contains(r.Status))
                    .Where(r => r.PullRequestUrl is not null)
                    .GroupBy(r => r.PullRequestUrl!)
                    .Select(g => g.OrderByDescending(r => r.CreatedAt).First())
                    .ToList();
                return Task.FromResult<IReadOnlyList<AgentRunHandle>>(results);
            }
        }

        private static bool IsTerminal(RunLifecycleState s) =>
            s is RunLifecycleState.Completed or RunLifecycleState.Failed
                or RunLifecycleState.Cancelled or RunLifecycleState.CleanedUp;
    }

    private sealed class InMemoryLifecycleEventStore : ILifecycleEventStore
    {
        private readonly object _lock = new();
        private readonly List<LifecycleEvent> _events = new();

        public Task AppendAsync(LifecycleEvent evt, CancellationToken ct)
        {
            var id = evt.Id;
            if (string.IsNullOrWhiteSpace(id))
                id = $"evt_{Guid.NewGuid():N}";

            lock (_lock) { _events.Add(evt with { Id = id }); }
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<LifecycleEvent>> ListByRunIdAsync(string runId, CancellationToken ct)
        {
            lock (_lock)
            {
                return Task.FromResult<IReadOnlyList<LifecycleEvent>>(
                    _events.Where(e => e.RunId == runId).OrderBy(e => e.CreatedAt).ToList());
            }
        }

        public Task<bool> ExistsByEventIdAsync(string runId, string eventId, CancellationToken ct)
        {
            lock (_lock)
            {
                return Task.FromResult(_events.Any(e => e.RunId == runId && e.EventId == eventId));
            }
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
            var existing = _items.Values.FirstOrDefault(
                i => i.Source == candidate.Source && i.ExternalId == candidate.ExternalId);
            if (existing is not null)
            {
                _items[existing.Id] = candidate with { Id = existing.Id };
                return Task.FromResult(candidate with { Id = existing.Id });
            }

            var id = candidate.Id.Length > 0 ? candidate.Id : $"wi_{Guid.NewGuid():N}";
            var item = candidate with { Id = id };
            _items[id] = item;
            return Task.FromResult(item);
        }
    }

    private sealed class StubWorkSource : IWorkSource
    {
        public Task<IReadOnlyList<WorkCandidate>> FindEligibleAsync(
            WorkQuery query, CancellationToken ct)
        {
            return Task.FromResult<IReadOnlyList<WorkCandidate>>([]);
        }

        public Task<ClaimResult> TryClaimAsync(
            WorkCandidate candidate, ClaimRequest claim, CancellationToken ct)
        {
            return Task.FromResult(new ClaimResult { Success = true });
        }

        public Task UpdateStatusAsync(
            ExternalWorkRef workRef, ExternalWorkStatus status, CancellationToken ct)
        {
            return Task.CompletedTask;
        }

        public Task AddCommentAsync(
            ExternalWorkRef workRef, string comment, CancellationToken ct)
        {
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<WorkItemComment>> GetCommentsAsync(
            ExternalWorkRef workRef, int maxComments, CancellationToken ct)
        {
            return Task.FromResult<IReadOnlyList<WorkItemComment>>(Array.Empty<WorkItemComment>());
        }

        public Task ReleaseClaimAsync(
            ReleaseClaimRequest request, CancellationToken ct)
        {
            return Task.CompletedTask;
        }

        public Task<ReworkReactivateResult> ReactivateForReworkAsync(
            ReworkReactivateRequest request, CancellationToken ct)
        {
            return Task.FromResult(new ReworkReactivateResult { Success = true });
        }
    }
}

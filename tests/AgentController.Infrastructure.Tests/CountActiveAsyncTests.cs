using AgentController.Application;
using AgentController.Domain;
using AgentController.Infrastructure.Data;
using AgentController.Infrastructure.Data.Entities;
using AgentController.Infrastructure.Data.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AgentController.Infrastructure.Tests;

/// <summary>
/// Regression tests for <see cref="IAgentRunStore.CountActiveAsync"/>.
///
/// Verifies that only runs whose agent runtime is actively executing or
/// being staged (Claimed through AwaitingResult) count toward the
/// concurrency limit. Post-execution states and terminal states must
/// NOT consume a concurrency slot.
/// </summary>
public class CountActiveAsyncTests : IAsyncLifetime, IDisposable
{
    private SqliteConnection? _connection;
    private AgentControllerDbContext? _db;
    private EfAgentRunStore? _store;
    private bool _disposed;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();

        var options = new DbContextOptionsBuilder<AgentControllerDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new AgentControllerDbContext(options);
        await _db!.Database.EnsureCreatedAsync();

        _store = new EfAgentRunStore(_db!);
    }

    public async Task DisposeAsync()
    {
        Dispose(true);
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    private void Dispose(bool disposing)
    {
        if (_disposed)
            return;

        if (disposing)
        {
            _db?.Dispose();
            _connection?.Dispose();
        }

        _disposed = true;
    }

    // ── Helpers ──────────────────────────────────────────────────────

    /// <summary>Seed a single AgentRunEntity in the given state.</summary>
    private async Task SeedRunAsync(RunLifecycleState state)
    {
        var entity = new AgentRunEntity
        {
            Id = $"run_{state:D}",
            WorkItemId = $"wi_{state:D}",
            WorkerId = "test-worker",
            RuntimeType = "test",
            Status = (int)state,
            RunAttempt = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        _db!.AgentRuns.Add(entity);
        await _db.SaveChangesAsync();
    }

    /// <summary>Seed an AgentRunEntity with an explicit ID (for duplicate-state scenarios).</summary>
    private async Task SeedRunWithIdAsync(string id, RunLifecycleState state)
    {
        var entity = new AgentRunEntity
        {
            Id = id,
            WorkItemId = $"wi_{id}",
            WorkerId = "test-worker",
            RuntimeType = "test",
            Status = (int)state,
            RunAttempt = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        _db!.AgentRuns.Add(entity);
        await _db.SaveChangesAsync();
    }

    /// <summary>Clear all seeded runs from the database.</summary>
    private async Task ClearRunsAsync()
    {
        _db!.AgentRuns.RemoveRange(_db.AgentRuns);
        await _db.SaveChangesAsync();
    }

    // ── Active states: Claimed through AwaitingResult ────────────────

    [Theory]
    [InlineData(RunLifecycleState.Claimed)]
    [InlineData(RunLifecycleState.EnvironmentProvisioning)]
    [InlineData(RunLifecycleState.EnvironmentReady)]
    [InlineData(RunLifecycleState.RepositoryCloning)]
    [InlineData(RunLifecycleState.RepositoryReady)]
    [InlineData(RunLifecycleState.ContextInjected)]
    [InlineData(RunLifecycleState.AgentStarting)]
    [InlineData(RunLifecycleState.AgentRunning)]
    [InlineData(RunLifecycleState.AwaitingResult)]
    public async Task CountActiveAsync_ActiveStates_AreCounted(RunLifecycleState state)
    {
        // Arrange
        await SeedRunAsync(state);

        // Act
        var count = await _store!.CountActiveAsync(CancellationToken.None);

        // Assert
        Assert.Equal(1, count);

        // Cleanup
        await ClearRunsAsync();
    }

    // ── Non-active post-execution states ─────────────────────────────

    [Theory]
    [InlineData(RunLifecycleState.ResultReceived)]
    [InlineData(RunLifecycleState.PrOpened)]
    [InlineData(RunLifecycleState.BranchPushed)]
    [InlineData(RunLifecycleState.NeedsHuman)]
    public async Task CountActiveAsync_PostExecutionStates_AreNotCounted(RunLifecycleState state)
    {
        // Arrange
        await SeedRunAsync(state);

        // Act
        var count = await _store!.CountActiveAsync(CancellationToken.None);

        // Assert — post-execution states must NOT consume a concurrency slot
        Assert.Equal(0, count);

        // Cleanup
        await ClearRunsAsync();
    }

    // ── Terminal states ──────────────────────────────────────────────

    [Theory]
    [InlineData(RunLifecycleState.Completed)]
    [InlineData(RunLifecycleState.Failed)]
    [InlineData(RunLifecycleState.Cancelled)]
    [InlineData(RunLifecycleState.CleanupPending)]
    [InlineData(RunLifecycleState.CleanedUp)]
    public async Task CountActiveAsync_TerminalStates_AreNotCounted(RunLifecycleState state)
    {
        // Arrange
        await SeedRunAsync(state);

        // Act
        var count = await _store!.CountActiveAsync(CancellationToken.None);

        // Assert — terminal states must NOT consume a concurrency slot
        Assert.Equal(0, count);

        // Cleanup
        await ClearRunsAsync();
    }

    // ── Queued state (not yet claimed) ───────────────────────────────

    [Fact]
    public async Task CountActiveAsync_QueuedState_IsNotCounted()
    {
        // Arrange — Queued is not yet claimed, so the agent runtime is not
        // actively executing or being staged.
        await SeedRunAsync(RunLifecycleState.Queued);

        // Act
        var count = await _store!.CountActiveAsync(CancellationToken.None);

        // Assert
        Assert.Equal(0, count);

        // Cleanup
        await ClearRunsAsync();
    }

    // ── Mixed states: only active states contribute ──────────────────

    [Fact]
    public async Task CountActiveAsync_MixedStates_CountsOnlyActive()
    {
        // Arrange — seed one run in every lifecycle state
        foreach (var state in Enum.GetValues<RunLifecycleState>())
        {
            await SeedRunAsync(state);
        }

        // Act
        var count = await _store!.CountActiveAsync(CancellationToken.None);

        // Assert — only Claimed through AwaitingResult should be counted
        var expectedActive = Enum.GetValues<RunLifecycleState>()
            .Count(s => s.IsActiveForConcurrency());

        Assert.Equal(expectedActive, count);
    }

    // ── Regression: post-execution runs should not block new jobs ────

    [Fact]
    public async Task CountActiveAsync_PostExecutionRuns_DoNotBlockNewJobs()
    {
        // Scenario: MaxConcurrentRuns is 3, but all 3 existing runs are in
        // post-execution states (PrOpened, BranchPushed). The worker should
        // see availableSlots > 0 and discover candidates.

        const int maxConcurrentRuns = 3;

        // Arrange — seed runs in post-execution states that previously
        // (incorrectly) consumed concurrency slots
        await SeedRunAsync(RunLifecycleState.PrOpened);
        await SeedRunAsync(RunLifecycleState.BranchPushed);
        await SeedRunAsync(RunLifecycleState.ResultReceived);

        // Act
        var activeCount = await _store!.CountActiveAsync(CancellationToken.None);
        var availableSlots = maxConcurrentRuns - activeCount;

        // Assert — all slots must be available because no run is actively
        // executing
        Assert.Equal(0, activeCount);
        Assert.Equal(maxConcurrentRuns, availableSlots);
    }

    [Fact]
    public async Task CountActiveAsync_NeedsHumanRuns_DoNotBlockNewJobs()
    {
        // Scenario: MaxConcurrentRuns is 2, but 2 runs are in NeedsHuman.
        // The agent runtime has stopped waiting for input, so new jobs
        // should still be discoverable.

        const int maxConcurrentRuns = 2;

        // Arrange — seed two distinct runs in NeedsHuman
        await SeedRunWithIdAsync("needs-human-1", RunLifecycleState.NeedsHuman);
        await SeedRunWithIdAsync("needs-human-2", RunLifecycleState.NeedsHuman);

        // Act
        var activeCount = await _store!.CountActiveAsync(CancellationToken.None);
        var availableSlots = maxConcurrentRuns - activeCount;

        // Assert
        Assert.Equal(0, activeCount);
        Assert.Equal(maxConcurrentRuns, availableSlots);
    }

    // ── Regression: mixed active + post-execution ────────────────────

    [Fact]
    public async Task CountActiveAsync_MixedActiveAndPostExecution_CountsOnlyActive()
    {
        // Scenario: 2 active runs + 3 post-execution runs.
        // activeCount should be 2, not 5.

        await SeedRunAsync(RunLifecycleState.AgentRunning);
        await SeedRunAsync(RunLifecycleState.AwaitingResult);
        await SeedRunAsync(RunLifecycleState.PrOpened);
        await SeedRunAsync(RunLifecycleState.BranchPushed);
        await SeedRunAsync(RunLifecycleState.NeedsHuman);

        var activeCount = await _store!.CountActiveAsync(CancellationToken.None);

        Assert.Equal(2, activeCount);
    }
}

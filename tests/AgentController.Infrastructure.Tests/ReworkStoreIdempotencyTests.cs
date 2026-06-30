using AgentController.Application;
using AgentController.Domain;
using AgentController.Infrastructure.Data;
using AgentController.Infrastructure.Data.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AgentController.Infrastructure.Tests;

/// <summary>
/// Idempotency tests for <see cref="IReworkCycleStore"/> and
/// <see cref="IReworkFeedbackStore"/>.
///
/// These tests verify the hard guards that prevent double-materialization
/// and ensure safe retry semantics across the feedback pipeline:
///
/// IReworkCycleStore:
///   - CreateAsync: unique FeedbackBundleId guard (throws on duplicate)
///   - MarkConsumedAsync: idempotent (no-op if already consumed or missing)
///
/// IReworkFeedbackStore:
///   - UpsertAsync: upsert semantics (update existing, insert new)
///   - MarkSupersededAsync: idempotent (no-op if already Superseded or Soaked)
///   - MarkSoakedAsync: idempotent (no-op if not in Watching status)
/// </summary>
public class ReworkStoreIdempotencyTests : IAsyncLifetime, IDisposable
{
    private SqliteConnection? _connection;
    private AgentControllerDbContext? _db;
    private EfReworkCycleStore? _cycleStore;
    private EfReworkFeedbackStore? _feedbackStore;
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

        _cycleStore = new EfReworkCycleStore(_db!);
        _feedbackStore = new EfReworkFeedbackStore(_db!);
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

    // ── IReworkCycleStore: CreateAsync unique FeedbackBundleId guard ──

    [Fact]
    public async Task ReworkCycleStore_CreateAsync_SucceedsOnFirstCall()
    {
        // Arrange + Act: create a cycle with a unique FeedbackBundleId.
        var cycle = await _cycleStore!.CreateAsync(
            workItemId: "wi-1",
            cycleNumber: 1,
            priorRunId: "run-prev",
            branchName: "feature/test",
            pullRequestUrl: "https://dev.azure.com/pr/42",
            baseCommitSha: "abc123def456",
            feedbackBundleJson: "[]",
            feedbackBundleId: "bundle-hash-001",
            cancellationToken: CancellationToken.None);

        // Assert: cycle is created with Pending status.
        Assert.NotNull(cycle);
        Assert.Equal("wi-1", cycle.WorkItemId);
        Assert.Equal(1, cycle.CycleNumber);
        Assert.Equal(ReworkCycleStatus.Pending, cycle.Status);
        Assert.Equal("bundle-hash-001", cycle.FeedbackBundleId);
        Assert.Null(cycle.ConsumedAt);
        Assert.Null(cycle.NewRunId);
    }

    [Fact]
    public async Task ReworkCycleStore_CreateAsync_ThrowsOnDuplicateFeedbackBundleId()
    {
        // Arrange: create a cycle with a FeedbackBundleId.
        await _cycleStore!.CreateAsync(
            "wi-1", 1, "run-prev", "feature/test",
            "https://dev.azure.com/pr/42", "abc123def456",
            "[]", "bundle-hash-dup", CancellationToken.None);

        // Act + Assert: second create with the same FeedbackBundleId
        // must throw a unique constraint violation (hard idempotency guard).
        var ex = await Assert.ThrowsAsync<DbUpdateException>(()
            => _cycleStore!.CreateAsync(
                "wi-1", 2, "run-prev", "feature/test",
                "https://dev.azure.com/pr/42", "abc123def456",
                "[]", "bundle-hash-dup", CancellationToken.None));

        // The inner SQLite exception indicates a UNIQUE constraint failure.
        var innerMessage = ex.InnerException?.Message ?? ex.Message;
        Assert.Contains("UNIQUE constraint", innerMessage);
    }

    [Fact]
    public async Task ReworkCycleStore_CreateAsync_AllowsDifferentBundleIdsForSameWorkItem()
    {
        // Arrange + Act: create two cycles for the same work item with
        // different FeedbackBundleIds (different rework bundles).
        var cycle1 = await _cycleStore!.CreateAsync(
            "wi-1", 1, "run-prev", "feature/test",
            "https://dev.azure.com/pr/42", "abc123def456",
            "[]", "bundle-hash-a", CancellationToken.None);

        var cycle2 = await _cycleStore!.CreateAsync(
            "wi-1", 2, "run-prev", "feature/test",
            "https://dev.azure.com/pr/42", "abc123def456",
            "[]", "bundle-hash-b", CancellationToken.None);

        // Assert: both succeed with different IDs and cycle numbers.
        Assert.NotEqual(cycle1.Id, cycle2.Id);
        Assert.Equal(1, cycle1.CycleNumber);
        Assert.Equal(2, cycle2.CycleNumber);
    }

    // ── IReworkCycleStore: MarkConsumedAsync idempotency ────────────

    [Fact]
    public async Task ReworkCycleStore_MarkConsumedAsync_TransitionsPendingToConsumed()
    {
        // Arrange: create a Pending cycle.
        var cycle = await _cycleStore!.CreateAsync(
            "wi-1", 1, "run-prev", "feature/test",
            "https://dev.azure.com/pr/42", "abc123def456",
            "[]", "bundle-consume-001", CancellationToken.None);

        // Act: mark consumed.
        await _cycleStore.MarkConsumedAsync(cycle.Id, "run-new", CancellationToken.None);

        // Assert: cycle is now Consumed with NewRunId set.
        var allCycles = await _db!.ReworkCycles.ToListAsync();
        var entity = allCycles.First(e => e.Id == cycle.Id);
        Assert.Equal((int)ReworkCycleStatus.Consumed, entity.Status);
        Assert.Equal("run-new", entity.NewRunId);
        Assert.NotNull(entity.ConsumedAt);
    }

    [Fact]
    public async Task ReworkCycleStore_MarkConsumedAsync_IsIdempotentWhenAlreadyConsumed()
    {
        // Arrange: create a cycle and consume it.
        var cycle = await _cycleStore!.CreateAsync(
            "wi-1", 1, "run-prev", "feature/test",
            "https://dev.azure.com/pr/42", "abc123def456",
            "[]", "bundle-idempotent-001", CancellationToken.None);

        await _cycleStore.MarkConsumedAsync(cycle.Id, "run-new-1", CancellationToken.None);

        // Act: call MarkConsumedAsync again with a different run ID.
        await _cycleStore.MarkConsumedAsync(cycle.Id, "run-new-2", CancellationToken.None);

        // Assert: no exception, and the original consumption is preserved.
        var allCycles = await _db!.ReworkCycles.ToListAsync();
        var entity = allCycles.First(e => e.Id == cycle.Id);
        Assert.Equal((int)ReworkCycleStatus.Consumed, entity.Status);
        Assert.Equal("run-new-1", entity.NewRunId); // Original run ID preserved.
    }

    [Fact]
    public async Task ReworkCycleStore_MarkConsumedAsync_NoOpForNonExistentId()
    {
        // Act: mark consumed with an ID that doesn't exist.
        await _cycleStore!.MarkConsumedAsync("rcycle_nonexistent", "run-new", CancellationToken.None);

        // Assert: no exception thrown (silent no-op).
        var allCycles = await _db!.ReworkCycles.ToListAsync();
        Assert.Empty(allCycles);
    }

    [Fact]
    public async Task ReworkCycleStore_MarkConsumedAsync_RemovesFromPendingList()
    {
        // Arrange: create a Pending cycle.
        var cycle = await _cycleStore!.CreateAsync(
            "wi-1", 1, "run-prev", "feature/test",
            "https://dev.azure.com/pr/42", "abc123def456",
            "[]", "bundle-pending-001", CancellationToken.None);

        // Verify it appears in Pending.
        var pending = await _cycleStore.GetPendingForWorkItemAsync("wi-1", CancellationToken.None);
        Assert.NotNull(pending);
        Assert.Equal(cycle.Id, pending!.Id);

        // Act: consume it.
        await _cycleStore.MarkConsumedAsync(cycle.Id, "run-new", CancellationToken.None);

        // Assert: no longer appears in Pending.
        var pendingAfter = await _cycleStore.GetPendingForWorkItemAsync("wi-1", CancellationToken.None);
        Assert.Null(pendingAfter);

        // But it appears in Consumed.
        var consumed = await _cycleStore.ListConsumedAsync(CancellationToken.None);
        Assert.Single(consumed);
        Assert.Equal(cycle.Id, consumed[0].Id);
        Assert.Equal("run-new", consumed[0].NewRunId);
    }

    // ── IReworkFeedbackStore: UpsertAsync idempotency ──────────────

    [Fact]
    public async Task ReworkFeedbackStore_UpsertAsync_InsertsNewRow()
    {
        // Arrange + Act: upsert a new feedback row.
        var now = DateTimeOffset.UtcNow;
        var row = await _feedbackStore!.UpsertAsync(
            "run-1", "pr-100", "bundle-001", "[]", 1,
            now, now, ReworkFeedbackStatus.Watching, CancellationToken.None);

        // Assert: row is created.
        Assert.NotNull(row);
        Assert.Equal("pr-100", row.PullRequestId);
        Assert.Equal("bundle-001", row.FeedbackBundleId);
        Assert.Equal(ReworkFeedbackStatus.Watching, row.Status);
    }

    [Fact]
    public async Task ReworkFeedbackStore_UpsertAsync_UpdatesExistingRow()
    {
        // Arrange: upsert a new row.
        var now = DateTimeOffset.UtcNow;
        var first = await _feedbackStore!.UpsertAsync(
            "run-1", "pr-100", "bundle-002", "[]", 1,
            now, now, ReworkFeedbackStatus.Watching, CancellationToken.None);

        var originalId = first.Id;

        // Act: upsert again with the same (PullRequestId, FeedbackBundleId)
        // but different data.
        var updated = await _feedbackStore.UpsertAsync(
            "run-2", "pr-100", "bundle-002", "[]", 3,
            now.AddMinutes(-5), now.AddMinutes(-1),
            ReworkFeedbackStatus.Watching, CancellationToken.None);

        // Assert: same row ID (update, not insert), data is updated.
        Assert.Equal(originalId, updated.Id);
        Assert.Equal("run-2", updated.OriginatingRunId);
        Assert.Equal(3, updated.ThreadCount);
        Assert.Equal(now.AddMinutes(-5), updated.FirstQualifyingCommentAt);
        Assert.Equal(now.AddMinutes(-1), updated.LastQualifyingCommentAt);

        // Only one row in the database.
        var allRows = await _db!.ReworkFeedback
            .Where(e => e.PullRequestId == "pr-100" && e.FeedbackBundleId == "bundle-002")
            .ToListAsync();
        Assert.Single(allRows);
    }

    [Fact]
    public async Task ReworkFeedbackStore_UpsertAsync_DoesNotCreateDuplicates()
    {
        // Arrange + Act: upsert the same key multiple times.
        var now = DateTimeOffset.UtcNow;

        for (int i = 0; i < 5; i++)
        {
            await _feedbackStore!.UpsertAsync(
                "run-1", "pr-101", "bundle-dedup", "[]", i + 1,
                now, now, ReworkFeedbackStatus.Watching, CancellationToken.None);
        }

        // Assert: only one row exists.
        var allRows = await _db!.ReworkFeedback
            .Where(e => e.PullRequestId == "pr-101" && e.FeedbackBundleId == "bundle-dedup")
            .ToListAsync();
        Assert.Single(allRows);

        // Last ThreadCount should be 5 (from the last upsert).
        Assert.Equal(5, allRows[0].ThreadCount);
    }

    // ── IReworkFeedbackStore: MarkSupersededAsync idempotency ──────

    [Fact]
    public async Task ReworkFeedbackStore_MarkSupersededAsync_IsIdempotent()
    {
        // Arrange: create a Watching row and supersede it.
        var now = DateTimeOffset.UtcNow;
        var row = await _feedbackStore!.UpsertAsync(
            "run-1", "pr-200", "bundle-sup-001", "[]", 1,
            now, now, ReworkFeedbackStatus.Watching, CancellationToken.None);

        await _feedbackStore.MarkSupersededAsync(row.Id, CancellationToken.None);

        // Act: call MarkSupersededAsync again.
        await _feedbackStore.MarkSupersededAsync(row.Id, CancellationToken.None);

        // Assert: no exception, row remains Superseded.
        var allRows = await _db!.ReworkFeedback.ToListAsync();
        var entity = allRows.First(e => e.Id == row.Id);
        Assert.Equal((int)ReworkFeedbackStatus.Superseded, entity.Status);
    }

    [Fact]
    public async Task ReworkFeedbackStore_MarkSupersededAsync_NoOpOnSoakedRow()
    {
        // Arrange: create a row and mark it Soaked.
        var lastComment = DateTimeOffset.UtcNow.AddMinutes(-10);
        var row = await _feedbackStore!.UpsertAsync(
            "run-1", "pr-201", "bundle-sup-002", "[]", 1,
            lastComment, lastComment, ReworkFeedbackStatus.Watching, CancellationToken.None);

        await _feedbackStore.MarkSoakedAsync(row.Id, CancellationToken.None);

        // Act: try to supersede a Soaked row.
        await _feedbackStore.MarkSupersededAsync(row.Id, CancellationToken.None);

        // Assert: row remains Soaked.
        var allRows = await _db!.ReworkFeedback.ToListAsync();
        var entity = allRows.First(e => e.Id == row.Id);
        Assert.Equal((int)ReworkFeedbackStatus.Soaked, entity.Status);
    }

    [Fact]
    public async Task ReworkFeedbackStore_MarkSupersededAsync_NoOpOnNonExistentId()
    {
        // Act: supersede a non-existent ID.
        await _feedbackStore!.MarkSupersededAsync("rfeedback_nonexistent", CancellationToken.None);

        // Assert: no exception (silent no-op).
        var allRows = await _db!.ReworkFeedback.ToListAsync();
        Assert.Empty(allRows);
    }

    // ── IReworkFeedbackStore: MarkSoakedAsync idempotency ──────────

    [Fact]
    public async Task ReworkFeedbackStore_MarkSoakedAsync_IsIdempotent()
    {
        // Arrange: create a Watching row and mark it Soaked.
        var lastComment = DateTimeOffset.UtcNow.AddMinutes(-10);
        var row = await _feedbackStore!.UpsertAsync(
            "run-1", "pr-300", "bundle-soak-001", "[]", 1,
            lastComment, lastComment, ReworkFeedbackStatus.Watching, CancellationToken.None);

        var soaked = await _feedbackStore.MarkSoakedAsync(row.Id, CancellationToken.None);
        Assert.NotNull(soaked);
        Assert.Equal(ReworkFeedbackStatus.Soaked, soaked.Status);

        // Act: call MarkSoakedAsync again on the same row.
        var result = await _feedbackStore.MarkSoakedAsync(row.Id, CancellationToken.None);

        // Assert: returns null (idempotent guard — already Soaked, not Watching).
        Assert.Null(result);
    }

    [Fact]
    public async Task ReworkFeedbackStore_MarkSoakedAsync_ReturnsNullForNonExistentId()
    {
        // Act + Assert: mark soaked with a non-existent ID returns null.
        var result = await _feedbackStore!.MarkSoakedAsync(
            "rfeedback_nonexistent", CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public async Task ReworkFeedbackStore_MarkSoakedAsync_ReturnsNullForSupersededRow()
    {
        // Arrange: create a row and supersede it.
        var now = DateTimeOffset.UtcNow;
        var row = await _feedbackStore!.UpsertAsync(
            "run-1", "pr-301", "bundle-soak-002", "[]", 1,
            now, now, ReworkFeedbackStatus.Watching, CancellationToken.None);

        await _feedbackStore.MarkSupersededAsync(row.Id, CancellationToken.None);

        // Act: try to mark a Superseded row as Soaked.
        var result = await _feedbackStore.MarkSoakedAsync(row.Id, CancellationToken.None);

        // Assert: returns null (only transitions from Watching).
        Assert.Null(result);
    }
}

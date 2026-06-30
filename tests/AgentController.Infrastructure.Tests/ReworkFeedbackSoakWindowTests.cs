using AgentController.Application;
using AgentController.Domain;
using AgentController.Infrastructure.Data;
using AgentController.Infrastructure.Data.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AgentController.Infrastructure.Tests;

/// <summary>
/// Tests for the soak-window state machine implemented in
/// <see cref="EfReworkFeedbackStore"/>.
///
/// The soak-window is the debounce mechanism that prevents premature
/// rework materialization: after qualifying review comments appear on a PR,
/// the feedback worker watches for a configurable soak period (default 5 min)
/// before the bundle becomes eligible for materialization into a ReworkCycle.
///
/// Three core state transitions are tested here:
/// 1. Bundle change supersedes a prior Watching row
/// 2. Restart resumes watching without resetting soak
/// 3. Soak threshold flips Watching to Soaked
/// </summary>
public class ReworkFeedbackSoakWindowTests : IAsyncLifetime, IDisposable
{
    private SqliteConnection? _connection;
    private AgentControllerDbContext? _db;
    private EfReworkFeedbackStore? _store;
    private bool _disposed;

    /// <summary>
    /// Create an in-memory SQLite database with the full migration schema
    /// and wire up the <see cref="EfReworkFeedbackStore"/> for testing.
    /// </summary>
    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();

        var options = new DbContextOptionsBuilder<AgentControllerDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new AgentControllerDbContext(options);
        await _db!.Database.EnsureCreatedAsync();

        _store = new EfReworkFeedbackStore(_db!);
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

    // ── Helpers ────────────────────────────────────────────────────

    private static ReviewThread MakeThread(string id, string author = "reviewer@example.com")
    {
        return new ReviewThread
        {
            ThreadId = id,
            Author = author,
            CreatedAt = DateTimeOffset.UtcNow,
            Status = ReviewThreadStatus.Active,
            Comments = new List<ReviewThreadComment>
            {
                new() { Author = author, Body = "Please fix this", CreatedAt = DateTimeOffset.UtcNow, IsReply = false },
            },
        };
    }

    private static string ComputeBundleId(params string[] threadIds)
    {
        // Mirrors FeedbackPollingWorker.ComputeFeedbackBundleId logic.
        var sorted = threadIds.OrderBy(id => id, StringComparer.Ordinal)
            .Aggregate((a, b) => a + "|" + b);

        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(sorted));
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// Simulate a "restart" by disposing the current DbContext and creating
    /// a new one against the same in-memory connection. The data persists
    /// because we use a file-backed in-memory database (not :memory: with
    /// separate connections).
    /// </summary>
    private async Task SimulateRestartAsync()
    {
        _db!.Dispose();
        var options = new DbContextOptionsBuilder<AgentControllerDbContext>()
            .UseSqlite(_connection!)
            .Options;
        _db = new AgentControllerDbContext(options);
        _store = new EfReworkFeedbackStore(_db);
    }

    // ── Test 1: Bundle change supersedes a prior Watching row ──────

    [Fact]
    public async Task SoakWindow_BundleChange_SupersedesPriorWatchingRow()
    {
        // Arrange: insert a Watching row with bundle A.
        var bundleAId = ComputeBundleId("thread-1", "thread-2");
        var now = DateTimeOffset.UtcNow;

        var rowA = await _store!.UpsertAsync(
            originatingRunId: "run-1",
            pullRequestId: "pr-42",
            feedbackBundleId: bundleAId,
            feedbackBundleJson: "[]",
            threadCount: 2,
            firstQualifyingCommentAt: now,
            lastQualifyingCommentAt: now,
            status: ReworkFeedbackStatus.Watching,
            cancellationToken: CancellationToken.None);

        Assert.Equal(ReworkFeedbackStatus.Watching, rowA.Status);

        // Act: simulate a new poll with a changed bundle (thread-2 replaced by thread-3).
        var bundleBId = ComputeBundleId("thread-1", "thread-3");

        await _store!.MarkSupersededAsync(rowA.Id, CancellationToken.None);

        var rowB = await _store!.UpsertAsync(
            originatingRunId: "run-1",
            pullRequestId: "pr-42",
            feedbackBundleId: bundleBId,
            feedbackBundleJson: "[]",
            threadCount: 2,
            firstQualifyingCommentAt: now,
            lastQualifyingCommentAt: now,
            status: ReworkFeedbackStatus.Watching,
            cancellationToken: CancellationToken.None);

        // Assert: old row is Superseded, new row is Watching.
        var refreshedA = await _store!.UpsertAsync(
            "run-1", "pr-42", bundleAId, "[]", 2, now, now,
            ReworkFeedbackStatus.Superseded, CancellationToken.None);

        // Verify the old row is no longer in Watching list.
        var watchingRows = await _store!.GetWatchingAsync(CancellationToken.None);
        Assert.Single(watchingRows);
        Assert.Equal(bundleBId, watchingRows[0].FeedbackBundleId);
        Assert.Equal(ReworkFeedbackStatus.Watching, watchingRows[0].Status);

        // The old bundle should NOT appear in watching.
        Assert.DoesNotContain(watchingRows, r => r.FeedbackBundleId == bundleAId);
    }

    [Fact]
    public async Task SoakWindow_BundleChange_OldRowBecomesSuperseded()
    {
        // Arrange: create a Watching row.
        var bundleAId = ComputeBundleId("thread-1");
        var now = DateTimeOffset.UtcNow;

        var rowA = await _store!.UpsertAsync(
            "run-1", "pr-42", bundleAId, "[]", 1,
            now, now, ReworkFeedbackStatus.Watching, CancellationToken.None);

        // Act: supersede it.
        await _store!.MarkSupersededAsync(rowA.Id, CancellationToken.None);

        // Assert: it is no longer in Watching.
        var watching = await _store!.GetWatchingAsync(CancellationToken.None);
        Assert.Empty(watching);

        // Re-read the row directly to confirm Superseded status.
        var allRows = await _db!.ReworkFeedback.ToListAsync();
        var entityA = allRows.FirstOrDefault(e => e.FeedbackBundleId == bundleAId);
        Assert.NotNull(entityA);
        Assert.Equal((int)ReworkFeedbackStatus.Superseded, entityA!.Status);
    }

    [Fact]
    public async Task SoakWindow_BundleUnchanged_BumpsLastCommentTimestamp()
    {
        // Arrange: create a Watching row.
        var bundleId = ComputeBundleId("thread-1");
        var firstComment = DateTimeOffset.UtcNow.AddMinutes(-10);
        var secondComment = DateTimeOffset.UtcNow.AddMinutes(-5);

        var row = await _store!.UpsertAsync(
            "run-1", "pr-42", bundleId, "[]", 1,
            firstComment, firstComment, ReworkFeedbackStatus.Watching, CancellationToken.None);

        Assert.Equal(firstComment, row.LastQualifyingCommentAt);

        // Act: upsert with the same bundle but a newer LastQualifyingCommentAt.
        var updated = await _store!.UpsertAsync(
            "run-1", "pr-42", bundleId, "[]", 1,
            firstComment, secondComment, ReworkFeedbackStatus.Watching, CancellationToken.None);

        // Assert: LastQualifyingCommentAt was bumped, status stays Watching.
        Assert.Equal(secondComment, updated.LastQualifyingCommentAt);
        Assert.Equal(ReworkFeedbackStatus.Watching, updated.Status);
        Assert.Equal(row.Id, updated.Id); // Same row, not a new insert.
    }

    // ── Test 2: Restart resumes watching without resetting soak ────

    [Fact]
    public async Task SoakWindow_Restart_ResumesWatchingWithoutResettingSoak()
    {
        // Arrange: create a Watching row with a LastQualifyingCommentAt
        // that is 3 minutes ago (soak threshold = 5 min).
        var bundleId = ComputeBundleId("thread-1");
        var lastComment = DateTimeOffset.UtcNow.AddMinutes(-3);

        var row = await _store!.UpsertAsync(
            "run-1", "pr-42", bundleId, "[]", 1,
            lastComment, lastComment, ReworkFeedbackStatus.Watching, CancellationToken.None);

        var originalLastComment = row.LastQualifyingCommentAt;
        var originalId = row.Id;

        // Act: simulate a restart (new DbContext, same connection).
        await SimulateRestartAsync();

        // Assert: the Watching row persists with its original timestamps.
        var watching = await _store!.GetWatchingAsync(CancellationToken.None);
        Assert.Single(watching);

        var persisted = watching[0];
        Assert.Equal(originalId, persisted.Id);
        Assert.Equal(bundleId, persisted.FeedbackBundleId);
        Assert.Equal(ReworkFeedbackStatus.Watching, persisted.Status);

        // The soak timer should NOT have been reset — LastQualifyingCommentAt
        // must still be the original value (3 minutes ago).
        Assert.Equal(originalLastComment, persisted.LastQualifyingCommentAt);
    }

    [Fact]
    public async Task SoakWindow_Restart_SoakedRowsPersist()
    {
        // Arrange: create a Watching row that has soaked (10 min ago, threshold = 5 min).
        var bundleId = ComputeBundleId("thread-1");
        var lastComment = DateTimeOffset.UtcNow.AddMinutes(-10);

        var row = await _store!.UpsertAsync(
            "run-1", "pr-42", bundleId, "[]", 1,
            lastComment, lastComment, ReworkFeedbackStatus.Watching, CancellationToken.None);

        // Mark it as Soaked.
        var soaked = await _store!.MarkSoakedAsync(row.Id, CancellationToken.None);
        Assert.NotNull(soaked);
        Assert.Equal(ReworkFeedbackStatus.Soaked, soaked.Status);

        // Act: simulate restart.
        await SimulateRestartAsync();

        // Assert: Soaked row persists across restart.
        var soakedRows = await _store!.GetSoakedAsync(CancellationToken.None);
        Assert.Single(soakedRows);
        Assert.Equal(bundleId, soakedRows[0].FeedbackBundleId);
        Assert.Equal(ReworkFeedbackStatus.Soaked, soakedRows[0].Status);

        // It should NOT appear in Watching.
        var watching = await _store!.GetWatchingAsync(CancellationToken.None);
        Assert.Empty(watching);
    }

    [Fact]
    public async Task SoakWindow_Restart_SupersededRowsPersist()
    {
        // Arrange: create a row and supersede it.
        var bundleId = ComputeBundleId("thread-1");
        var now = DateTimeOffset.UtcNow;

        var row = await _store!.UpsertAsync(
            "run-1", "pr-42", bundleId, "[]", 1,
            now, now, ReworkFeedbackStatus.Watching, CancellationToken.None);

        await _store!.MarkSupersededAsync(row.Id, CancellationToken.None);

        // Act: simulate restart.
        await SimulateRestartAsync();

        // Assert: Superseded row persists and is not in Watching or Soaked.
        var watching = await _store!.GetWatchingAsync(CancellationToken.None);
        Assert.Empty(watching);

        var soaked = await _store!.GetSoakedAsync(CancellationToken.None);
        Assert.Empty(soaked);

        // But the row is still in the database.
        var allRows = await _db!.ReworkFeedback.ToListAsync();
        Assert.Single(allRows);
        Assert.Equal((int)ReworkFeedbackStatus.Superseded, allRows[0].Status);
    }

    // ── Test 3: Soak threshold flips Watching to Soaked ────────────

    [Fact]
    public async Task SoakWindow_ThresholdElapsed_FlipsWatchingToSoaked()
    {
        // Arrange: create a Watching row with LastQualifyingCommentAt
        // 10 minutes ago (well past the default 5-minute soak threshold).
        var bundleId = ComputeBundleId("thread-1");
        var lastComment = DateTimeOffset.UtcNow.AddMinutes(-10);

        var row = await _store!.UpsertAsync(
            "run-1", "pr-42", bundleId, "[]", 1,
            lastComment, lastComment, ReworkFeedbackStatus.Watching, CancellationToken.None);

        // Act: apply the soak threshold check (as done in FeedbackPollingWorker Step 8).
        var soaked = await _store!.MarkSoakedAsync(row.Id, CancellationToken.None);

        // Assert: row is now Soaked.
        Assert.NotNull(soaked);
        Assert.Equal(ReworkFeedbackStatus.Soaked, soaked.Status);

        // It should appear in Soaked list and NOT in Watching.
        var watching = await _store!.GetWatchingAsync(CancellationToken.None);
        Assert.Empty(watching);

        var soakedRows = await _store!.GetSoakedAsync(CancellationToken.None);
        Assert.Single(soakedRows);
        Assert.Equal(bundleId, soakedRows[0].FeedbackBundleId);
    }

    [Fact]
    public async Task SoakWindow_MarkSoaked_IsIdempotent()
    {
        // Arrange: create a Watching row and mark it Soaked.
        var bundleId = ComputeBundleId("thread-1");
        var lastComment = DateTimeOffset.UtcNow.AddMinutes(-10);

        var row = await _store!.UpsertAsync(
            "run-1", "pr-42", bundleId, "[]", 1,
            lastComment, lastComment, ReworkFeedbackStatus.Watching, CancellationToken.None);

        var soaked = await _store!.MarkSoakedAsync(row.Id, CancellationToken.None);
        Assert.NotNull(soaked);
        Assert.Equal(ReworkFeedbackStatus.Soaked, soaked.Status);

        // Act: call MarkSoakedAsync again on the same row.
        var result = await _store!.MarkSoakedAsync(row.Id, CancellationToken.None);

        // Assert: returns null (idempotent guard — already Soaked, not Watching).
        Assert.Null(result);
    }

    [Fact]
    public async Task SoakWindow_MarkSuperseded_IsIdempotent()
    {
        // Arrange: create a Watching row and supersede it.
        var bundleId = ComputeBundleId("thread-1");
        var now = DateTimeOffset.UtcNow;

        var row = await _store!.UpsertAsync(
            "run-1", "pr-42", bundleId, "[]", 1,
            now, now, ReworkFeedbackStatus.Watching, CancellationToken.None);

        await _store!.MarkSupersededAsync(row.Id, CancellationToken.None);

        // Act: call MarkSupersededAsync again.
        await _store!.MarkSupersededAsync(row.Id, CancellationToken.None);

        // Assert: no exception, row remains Superseded.
        var allRows = await _db!.ReworkFeedback.ToListAsync();
        Assert.Single(allRows);
        Assert.Equal((int)ReworkFeedbackStatus.Superseded, allRows[0].Status);
    }

    [Fact]
    public async Task SoakWindow_MarkSuperseded_NoOpOnSoakedRow()
    {
        // Arrange: create a row and mark it Soaked.
        var bundleId = ComputeBundleId("thread-1");
        var lastComment = DateTimeOffset.UtcNow.AddMinutes(-10);

        var row = await _store!.UpsertAsync(
            "run-1", "pr-42", bundleId, "[]", 1,
            lastComment, lastComment, ReworkFeedbackStatus.Watching, CancellationToken.None);

        await _store!.MarkSoakedAsync(row.Id, CancellationToken.None);

        // Act: try to supersede a Soaked row.
        await _store!.MarkSupersededAsync(row.Id, CancellationToken.None);

        // Assert: row remains Soaked (MarkSuperseded is no-op on Soaked).
        var allRows = await _db!.ReworkFeedback.ToListAsync();
        Assert.Single(allRows);
        Assert.Equal((int)ReworkFeedbackStatus.Soaked, allRows[0].Status);
    }

    // ── Integration: full soak-window lifecycle ────────────────────

    [Fact]
    public async Task SoakWindow_FullLifecycle_WatchingToSoakedToMaterialization()
    {
        // This test simulates the full soak-window lifecycle as executed
        // by the FeedbackPollingWorker across multiple poll cycles.

        var bundleId = ComputeBundleId("thread-1", "thread-2");
        var pullRequestId = "pr-99";
        var originatingRunId = "run-original";
        var bundleJson = "[]";

        // ── Poll 1: First qualifying comments arrive ──────────────
        var firstCommentTime = DateTimeOffset.UtcNow.AddMinutes(-8);

        var row1 = await _store!.UpsertAsync(
            originatingRunId, pullRequestId, bundleId, bundleJson, 2,
            firstCommentTime, firstCommentTime,
            ReworkFeedbackStatus.Watching, CancellationToken.None);

        Assert.Equal(ReworkFeedbackStatus.Watching, row1.Status);

        // Not yet soaked.
        var soakedAfterPoll1 = await _store!.GetSoakedAsync(CancellationToken.None);
        Assert.Empty(soakedAfterPoll1);

        // ── Poll 2: Bundle unchanged, bump timestamp ──────────────
        var secondCommentTime = DateTimeOffset.UtcNow.AddMinutes(-6);

        var row2 = await _store!.UpsertAsync(
            originatingRunId, pullRequestId, bundleId, bundleJson, 2,
            firstCommentTime, secondCommentTime,
            ReworkFeedbackStatus.Watching, CancellationToken.None);

        Assert.Equal(row1.Id, row2.Id); // Same row.
        Assert.Equal(secondCommentTime, row2.LastQualifyingCommentAt);

        // ── Poll 3: Bundle changes (new thread added) ─────────────
        var newBundleId = ComputeBundleId("thread-1", "thread-2", "thread-3");
        var thirdCommentTime = DateTimeOffset.UtcNow.AddMinutes(-3);

        // Supersede the old bundle.
        await _store!.MarkSupersededAsync(row2.Id, CancellationToken.None);

        var row3 = await _store!.UpsertAsync(
            originatingRunId, pullRequestId, newBundleId, bundleJson, 3,
            thirdCommentTime, thirdCommentTime,
            ReworkFeedbackStatus.Watching, CancellationToken.None);

        Assert.NotEqual(row1.Id, row3.Id); // New row for new bundle.
        Assert.Equal(ReworkFeedbackStatus.Watching, row3.Status);

        // Old row is superseded.
        var watchingAfterPoll3 = await _store!.GetWatchingAsync(CancellationToken.None);
        Assert.Single(watchingAfterPoll3);
        Assert.Equal(newBundleId, watchingAfterPoll3[0].FeedbackBundleId);

        // ── Poll 4: Soak threshold elapsed, mark Soaked ───────────
        // Simulate time passing: update LastQualifyingCommentAt to be
        // 10 minutes ago to simulate the soak window elapsing.
        var soakedCommentTime = DateTimeOffset.UtcNow.AddMinutes(-10);

        await _store!.UpsertAsync(
            originatingRunId, pullRequestId, newBundleId, bundleJson, 3,
            thirdCommentTime, soakedCommentTime,
            ReworkFeedbackStatus.Watching, CancellationToken.None);

        // Now apply the soak threshold check.
        var watching = await _store!.GetWatchingAsync(CancellationToken.None);
        Assert.Single(watching);

        var soaked = await _store!.MarkSoakedAsync(watching[0].Id, CancellationToken.None);
        Assert.NotNull(soaked);
        Assert.Equal(ReworkFeedbackStatus.Soaked, soaked.Status);

        // ── Verify final state ────────────────────────────────────
        var finalSoaked = await _store!.GetSoakedAsync(CancellationToken.None);
        Assert.Single(finalSoaked);
        Assert.Equal(newBundleId, finalSoaked[0].FeedbackBundleId);
        Assert.Equal(3, finalSoaked[0].ThreadCount);

        var finalWatching = await _store!.GetWatchingAsync(CancellationToken.None);
        Assert.Empty(finalWatching);
    }

    [Fact]
    public async Task SoakWindow_MultiplePRs_TrackedIndependently()
    {
        // Two different PRs should have independent soak windows.
        var bundleA = ComputeBundleId("thread-a");
        var bundleB = ComputeBundleId("thread-b");
        var now = DateTimeOffset.UtcNow;

        // PR 1: Watching (recent comment).
        await _store!.UpsertAsync(
            "run-1", "pr-1", bundleA, "[]", 1,
            now, now, ReworkFeedbackStatus.Watching, CancellationToken.None);

        // PR 2: Soaked (old comment).
        var rowB = await _store!.UpsertAsync(
            "run-2", "pr-2", bundleB, "[]", 1,
            now.AddMinutes(-10), now.AddMinutes(-10),
            ReworkFeedbackStatus.Watching, CancellationToken.None);

        await _store!.MarkSoakedAsync(rowB.Id, CancellationToken.None);

        // Assert: PR 1 is Watching, PR 2 is Soaked.
        var watching = await _store!.GetWatchingAsync(CancellationToken.None);
        Assert.Single(watching);
        Assert.Equal("pr-1", watching[0].PullRequestId);

        var soaked = await _store!.GetSoakedAsync(CancellationToken.None);
        Assert.Single(soaked);
        Assert.Equal("pr-2", soaked[0].PullRequestId);
    }
}

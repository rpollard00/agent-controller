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
        var firstComment = now.AddMinutes(-10);
        var first = await _feedbackStore!.UpsertAsync(
            "run-1", "pr-100", "bundle-002", "[]", 1,
            firstComment, firstComment, ReworkFeedbackStatus.Watching, CancellationToken.None);

        var originalId = first.Id;

        // Act: upsert again with the same (PullRequestId, FeedbackBundleId)
        // carrying a genuinely newer qualifying comment (and a late-surfaced
        // earlier first comment).
        var newerComment = now.AddMinutes(-3);
        var olderFirst = now.AddMinutes(-15);
        var updated = await _feedbackStore.UpsertAsync(
            "run-2", "pr-100", "bundle-002", "[]", 3,
            olderFirst, newerComment, ReworkFeedbackStatus.Watching, CancellationToken.None);

        // Assert: same row ID (update, not insert). The snapshot/payload update,
        // the quiet-period anchor advances, and the earliest anchor moves earlier.
        Assert.Equal(originalId, updated.Id);
        Assert.Equal("run-2", updated.OriginatingRunId);
        Assert.Equal(3, updated.ThreadCount);
        Assert.Equal(olderFirst, updated.FirstQualifyingCommentAt);
        Assert.Equal(newerComment, updated.LastQualifyingCommentAt);

        // Only one row in the database.
        var allRows = await _db!.ReworkFeedback
            .Where(e => e.PullRequestId == "pr-100" && e.FeedbackBundleId == "bundle-002")
            .ToListAsync();
        Assert.Single(allRows);
    }

    [Fact]
    public async Task ReworkFeedbackStore_UpsertAsync_DoesNotCreateDuplicates()
    {
        // Arrange + Act: upsert the same key multiple times. Each replay carries
        // a genuinely newer qualifying comment so the payload advances.
        var now = DateTimeOffset.UtcNow;

        for (int i = 0; i < 5; i++)
        {
            var t = now.AddMinutes(i);
            await _feedbackStore!.UpsertAsync(
                "run-1", "pr-101", "bundle-dedup", "[]", i + 1,
                t, t, ReworkFeedbackStatus.Watching, CancellationToken.None);
        }

        // Assert: only one row exists.
        var allRows = await _db!.ReworkFeedback
            .Where(e => e.PullRequestId == "pr-101" && e.FeedbackBundleId == "bundle-dedup")
            .ToListAsync();
        Assert.Single(allRows);

        // Last ThreadCount should be 5 (from the most recent newer comment).
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

    [Fact]
    public async Task AssistanceFeedback_UpsertAndStoryReceipt_AreRestartSafe()
    {
        var now = DateTimeOffset.UtcNow;
        var request = new ReworkFeedbackUpsertRequest
        {
            RequestMode = ReworkRequestMode.Assistance,
            PullRequest = CreatePullRequest("repo-one", "42"),
            OriginatingRunId = null,
            FeedbackBundleId = "assistance-bundle-1",
            FeedbackBundleJson = "[]",
            ThreadCount = 0,
            FirstQualifyingCommentAt = now,
            LastQualifyingCommentAt = now,
            Status = ReworkFeedbackStatus.Watching,
            CorrelationId = "assistance-correlation-1",
        };

        var created = await _feedbackStore!.UpsertAsync(request, CancellationToken.None);
        var retried = await _feedbackStore.UpsertAsync(request, CancellationToken.None);

        Assert.Equal(created.Id, retried.Id);
        Assert.Equal(ReworkRequestMode.Assistance, retried.RequestMode);
        Assert.Equal(request.PullRequest.CanonicalKey, retried.PullRequest.CanonicalKey);
        Assert.Null(retried.OriginatingRunId);
        Assert.Equal(request.CorrelationId, retried.CorrelationId);

        await _feedbackStore.RecordAssistanceStoryAsync(
            created.Id,
            new AssistanceStoryReceipt
            {
                CorrelationId = request.CorrelationId,
                ExternalId = "ado-9001",
                Url = "https://dev.azure.com/example/_workitems/edit/9001",
            },
            CancellationToken.None);

        var recorded = await _feedbackStore.RecordAssistanceStoryAsync(
            created.Id,
            new AssistanceStoryReceipt
            {
                CorrelationId = request.CorrelationId,
                WorkItemId = "work-local-9001",
                ExternalId = "ado-9001",
                Url = "https://dev.azure.com/example/_workitems/edit/9001",
            },
            CancellationToken.None);

        var found = await _feedbackStore.GetByCorrelationIdAsync(
            request.CorrelationId,
            CancellationToken.None);
        Assert.NotNull(found);
        Assert.Equal(recorded.Id, found.Id);
        Assert.Equal("work-local-9001", found.AssistanceStoryWorkItemId);
        Assert.Equal("ado-9001", found.AssistanceStoryExternalId);
        Assert.Equal("https://dev.azure.com/example/_workitems/edit/9001", found.AssistanceStoryUrl);
        Assert.Single(await _db!.ReworkFeedback.ToListAsync());
    }

    [Fact]
    public async Task AssistanceFeedback_ReceiptRejectsConflictingExternalId()
    {
        var now = DateTimeOffset.UtcNow;
        var row = await _feedbackStore!.UpsertAsync(
            new ReworkFeedbackUpsertRequest
            {
                RequestMode = ReworkRequestMode.Assistance,
                PullRequest = CreatePullRequest("repo-one", "43"),
                FeedbackBundleId = "assistance-bundle-conflict",
                FeedbackBundleJson = "[]",
                FirstQualifyingCommentAt = now,
                LastQualifyingCommentAt = now,
                CorrelationId = "assistance-correlation-conflict",
            },
            CancellationToken.None);

        await _feedbackStore.RecordAssistanceStoryAsync(
            row.Id,
            new AssistanceStoryReceipt
            {
                CorrelationId = "assistance-correlation-conflict",
                ExternalId = "ado-1",
            },
            CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _feedbackStore.RecordAssistanceStoryAsync(
                row.Id,
                new AssistanceStoryReceipt
                {
                    CorrelationId = "assistance-correlation-conflict",
                    ExternalId = "ado-2",
                },
                CancellationToken.None));
    }

    [Fact]
    public async Task AssistanceFeedback_SameProviderIdAndBundleInDifferentRepos_AreIndependent()
    {
        var now = DateTimeOffset.UtcNow;
        var first = await _feedbackStore!.UpsertAsync(
            new ReworkFeedbackUpsertRequest
            {
                RequestMode = ReworkRequestMode.Assistance,
                PullRequest = CreatePullRequest("repo-one", "44"),
                FeedbackBundleId = "shared-bundle",
                FeedbackBundleJson = "[]",
                FirstQualifyingCommentAt = now,
                LastQualifyingCommentAt = now,
                CorrelationId = "correlation-repo-one",
            },
            CancellationToken.None);
        var second = await _feedbackStore.UpsertAsync(
            new ReworkFeedbackUpsertRequest
            {
                RequestMode = ReworkRequestMode.Assistance,
                PullRequest = CreatePullRequest("repo-two", "44"),
                FeedbackBundleId = "shared-bundle",
                FeedbackBundleJson = "[]",
                FirstQualifyingCommentAt = now,
                LastQualifyingCommentAt = now,
                CorrelationId = "correlation-repo-two",
            },
            CancellationToken.None);

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(2, await _db!.ReworkFeedback.CountAsync());
    }

    [Fact]
    public async Task AssistanceCycle_CreateAsync_IsIdempotentWithoutPriorRun()
    {
        var request = new ReworkCycleCreateRequest
        {
            RequestMode = ReworkRequestMode.Assistance,
            PullRequest = CreatePullRequest("repo-one", "50"),
            WorkItemId = "assistance-work-50",
            CycleNumber = 1,
            PriorRunId = null,
            BranchName = "feature/human-pr",
            PullRequestUrl = "https://dev.azure.com/example/_git/repo-one/pullrequest/50",
            BaseCommitSha = "abc123",
            FeedbackBundleJson = "[]",
            FeedbackBundleId = "assistance-cycle-bundle",
            CorrelationId = "assistance-cycle-correlation",
        };

        var created = await _cycleStore!.CreateAsync(request, CancellationToken.None);
        var retried = await _cycleStore.CreateAsync(request, CancellationToken.None);
        var found = await _cycleStore.GetByCorrelationIdAsync(
            request.CorrelationId!,
            CancellationToken.None);

        Assert.Equal(created.Id, retried.Id);
        Assert.NotNull(found);
        Assert.Equal(created.Id, found.Id);
        Assert.Equal(ReworkRequestMode.Assistance, found.RequestMode);
        Assert.Equal(request.PullRequest.CanonicalKey, found.PullRequest.CanonicalKey);
        Assert.Null(found.PriorRunId);
        Assert.Single(await _db!.ReworkCycles.ToListAsync());
    }

    [Fact]
    public async Task AssistanceCycle_SameBundleInDifferentRepos_IsAllowed()
    {
        var firstRequest = CreateAssistanceCycleRequest("repo-one", "51", "cycle-correlation-one");
        var secondRequest = CreateAssistanceCycleRequest("repo-two", "51", "cycle-correlation-two")
            with { WorkItemId = "assistance-work-two" };

        var first = await _cycleStore!.CreateAsync(firstRequest, CancellationToken.None);
        var second = await _cycleStore.CreateAsync(secondRequest, CancellationToken.None);

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(2, await _db!.ReworkCycles.CountAsync());
    }

    [Fact]
    public async Task AssistanceCycle_CreateAsync_NumbersAcrossStoriesForCanonicalPullRequest()
    {
        var firstRequest = CreateAssistanceCycleRequest(
            "repo-cycle-scope",
            "52",
            "cycle-scope-correlation-one") with
        {
            WorkItemId = "assistance-story-one",
            CycleNumber = 99,
            FeedbackBundleId = "cycle-scope-bundle-one",
        };
        var secondRequest = firstRequest with
        {
            WorkItemId = "assistance-story-two",
            CycleNumber = 99,
            FeedbackBundleId = "cycle-scope-bundle-two",
            CorrelationId = "cycle-scope-correlation-two",
        };

        var first = await _cycleStore!.CreateAsync(firstRequest, CancellationToken.None);
        var second = await _cycleStore.CreateAsync(secondRequest, CancellationToken.None);
        var max = await _cycleStore.GetMaxAssistanceCycleNumberAsync(
            firstRequest.PullRequest,
            CancellationToken.None);

        Assert.Equal(1, first.CycleNumber);
        Assert.Equal(2, second.CycleNumber);
        Assert.Equal(2, max);
    }

    [Fact]
    public async Task AssistanceCycle_CreateAsync_NumberingIsIndependentBetweenPullRequests()
    {
        var firstRequest = CreateAssistanceCycleRequest(
            "repo-cycle-scope",
            "53",
            "independent-correlation-one") with
        {
            WorkItemId = "shared-assistance-story",
            FeedbackBundleId = "independent-bundle-one",
        };
        var secondRequest = CreateAssistanceCycleRequest(
            "repo-cycle-scope",
            "54",
            "independent-correlation-two") with
        {
            WorkItemId = "shared-assistance-story",
            FeedbackBundleId = "independent-bundle-two",
        };

        var first = await _cycleStore!.CreateAsync(firstRequest, CancellationToken.None);
        var second = await _cycleStore.CreateAsync(secondRequest, CancellationToken.None);

        Assert.Equal(1, first.CycleNumber);
        Assert.Equal(1, second.CycleNumber);
    }

    [Fact]
    public async Task AssistanceCycle_CreateAsync_ConcurrentRequestsReceiveUniqueNumbers()
    {
        const int requestCount = 6;
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            $"agent-controller-assistance-cycles-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AgentControllerDbContext>()
            .UseSqlite($"Data Source={databasePath};Default Timeout=30")
            .Options;

        try
        {
            await using (var setup = new AgentControllerDbContext(options))
            {
                await setup.Database.EnsureCreatedAsync();
                await setup.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
            }

            var requests = Enumerable.Range(1, requestCount)
                .Select(async index =>
                {
                    await using var db = new AgentControllerDbContext(options);
                    var store = new EfReworkCycleStore(db);
                    var request = CreateAssistanceCycleRequest(
                        "repo-concurrent-cycles",
                        "55",
                        $"concurrent-correlation-{index}") with
                    {
                        WorkItemId = $"concurrent-story-{index}",
                        FeedbackBundleId = $"concurrent-bundle-{index}",
                    };

                    return await store.CreateAsync(request, CancellationToken.None);
                });

            var cycles = await Task.WhenAll(requests);

            Assert.Equal(
                Enumerable.Range(1, requestCount),
                cycles.Select(cycle => cycle.CycleNumber).Order());
            Assert.Equal(requestCount, cycles.Select(cycle => cycle.Id).Distinct().Count());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(databasePath);
            File.Delete($"{databasePath}-shm");
            File.Delete($"{databasePath}-wal");
        }
    }

    [Fact]
    public async Task RevivalCycle_CreateAsync_PreservesCallerSuppliedNumbering()
    {
        var cycle = await _cycleStore!.CreateAsync(
            workItemId: "revival-numbering-story",
            cycleNumber: 7,
            priorRunId: "revival-prior-run",
            branchName: "feature/revival-numbering",
            pullRequestUrl: "https://dev.azure.com/example/_git/repo/pullrequest/70",
            baseCommitSha: "abc123",
            feedbackBundleJson: "[]",
            feedbackBundleId: "revival-numbering-bundle",
            cancellationToken: CancellationToken.None);

        var max = await _cycleStore.GetMaxCycleNumberAsync(
            "revival-numbering-story",
            CancellationToken.None);

        Assert.Equal(7, cycle.CycleNumber);
        Assert.Equal(7, max);
    }

    private static ReworkCycleCreateRequest CreateAssistanceCycleRequest(
        string repositoryKey,
        string pullRequestId,
        string correlationId)
    {
        var pullRequest = CreatePullRequest(repositoryKey, pullRequestId);
        return new ReworkCycleCreateRequest
        {
            RequestMode = ReworkRequestMode.Assistance,
            PullRequest = pullRequest,
            WorkItemId = "assistance-work-one",
            CycleNumber = 1,
            BranchName = pullRequest.SourceBranch,
            PullRequestUrl = pullRequest.PullRequestUrl,
            BaseCommitSha = pullRequest.SourceCommitSha,
            FeedbackBundleJson = "[]",
            FeedbackBundleId = "bundle-shared-between-prs",
            CorrelationId = correlationId,
        };
    }

    private static PullRequestReference CreatePullRequest(
        string repositoryKey,
        string pullRequestId)
    {
        return new PullRequestReference
        {
            EnvironmentKey = "ado-main",
            RepositoryKey = repositoryKey,
            PullRequestId = pullRequestId,
            PullRequestUrl = $"https://dev.azure.com/example/_git/{repositoryKey}/pullrequest/{pullRequestId}",
            SourceBranch = "feature/human-pr",
            TargetBranch = "main",
            SourceCommitSha = "abc123",
        };
    }
}

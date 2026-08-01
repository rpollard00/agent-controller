using AgentController.Application;
using AgentController.Domain;
using AgentController.Infrastructure.Data;
using AgentController.Infrastructure.Data.Entities;
using AgentController.Infrastructure.Data.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AgentController.Infrastructure.Tests;

/// <summary>
/// Repository tests for the monotonic soak-persistence behavior of
/// <see cref="EfReworkFeedbackStore"/>.
///
/// Revival polling replays the same feedback bundle every cycle. The persisted
/// row must be the source of truth so a repeated Watching observation does not
/// restart the soak timer. These tests pin three guarantees:
///   1. Replaying an unchanged Watching observation is idempotent (row identity,
///      lifecycle status, soak baseline timestamps, and UpdatedAt are preserved).
///   2. A genuinely newer qualifying comment advances the quiet-period timestamp
///      and refreshes the bundle payload.
///   3. A Watching observation never regresses Soaked, Materialized, or
///      Superseded rows.
/// </summary>
public class ReworkFeedbackMonotonicPersistenceTests : IAsyncLifetime, IDisposable
{
    private SqliteConnection? _connection;
    private AgentControllerDbContext? _db;
    private EfReworkFeedbackStore? _store;
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

    private static ReworkFeedbackUpsertRequest RevivalRequest(
        string pullRequestId,
        string bundleId,
        string bundleJson,
        int threadCount,
        DateTimeOffset firstComment,
        DateTimeOffset lastComment,
        ReworkFeedbackStatus status = ReworkFeedbackStatus.Watching,
        string originatingRunId = "run-1") =>
        new()
        {
            RequestMode = ReworkRequestMode.Revival,
            PullRequest = new PullRequestReference { PullRequestId = pullRequestId },
            OriginatingRunId = originatingRunId,
            FeedbackBundleId = bundleId,
            FeedbackBundleJson = bundleJson,
            ThreadCount = threadCount,
            FirstQualifyingCommentAt = firstComment,
            LastQualifyingCommentAt = lastComment,
            Status = status,
        };

    private async Task<ReworkFeedbackEntity> GetEntityAsync(string id) =>
        await _db!.ReworkFeedback.FirstAsync(e => e.Id == id);

    // ── 1. Unchanged replay is idempotent ─────────────────────────

    [Fact]
    public async Task Upsert_UnchangedWatchingReplay_PreservesIdentityStatusAndSoakBaseline()
    {
        // Arrange: seed a Watching row.
        var firstComment = DateTimeOffset.UtcNow.AddMinutes(-10);
        var bundleJson = "[\"thread-1\"]";
        var created = await _store!.UpsertAsync(
            RevivalRequest("pr-1", "bundle-1", bundleJson, 1, firstComment, firstComment),
            CancellationToken.None);

        var createdUpdatedAt = created.UpdatedAt;

        // Give UtcNow room to advance so a bumped UpdatedAt would be detectable.
        await Task.Delay(20);

        // Act: replay the same observation. The replay deliberately carries a
        // different payload and thread count but no newer qualifying comment, so
        // the persisted row must reject the payload overwrite and stay put.
        var replayRequest = RevivalRequest(
            "pr-1", "bundle-1", "[\"changed\"]", 9, firstComment, firstComment);
        var replayed = await _store.UpsertAsync(replayRequest, CancellationToken.None);

        // Assert: row identity, lifecycle, soak baseline, payload, and UpdatedAt
        // are all preserved — the replay is a no-op for soak state.
        Assert.Equal(created.Id, replayed.Id);
        Assert.Equal(ReworkFeedbackStatus.Watching, replayed.Status);
        Assert.Equal(firstComment, replayed.FirstQualifyingCommentAt);
        Assert.Equal(firstComment, replayed.LastQualifyingCommentAt);
        Assert.Equal(bundleJson, replayed.FeedbackBundleJson);
        Assert.Equal(1, replayed.ThreadCount);
        Assert.Equal(createdUpdatedAt, replayed.UpdatedAt);

        var entity = await GetEntityAsync(created.Id);
        Assert.Equal(firstComment, entity.LastQualifyingCommentAt);
        Assert.Equal(createdUpdatedAt, entity.UpdatedAt);
        Assert.Equal(1, entity.ThreadCount);

        // Only one row exists.
        Assert.Single(await _db!.ReworkFeedback.ToListAsync());
    }

    [Fact]
    public async Task Upsert_UnchangedWatchingReplay_DoesNotRestartSoakAcrossCycles()
    {
        // Arrange: a Watching row whose quiet period has not yet elapsed.
        var firstComment = DateTimeOffset.UtcNow.AddMinutes(-3);
        await _store!.UpsertAsync(
            RevivalRequest("pr-2", "bundle-2", "[]", 2, firstComment, firstComment),
            CancellationToken.None);

        // Act: simulate several polling cycles replaying the same observation.
        for (var i = 0; i < 4; i++)
        {
            await _store!.UpsertAsync(
                RevivalRequest("pr-2", "bundle-2", "[]", 2, firstComment, firstComment),
                CancellationToken.None);
        }

        // Assert: the quiet-period anchor never moved, so the soak keeps
        // progressing instead of being restarted on every cycle.
        var watching = await _store!.GetWatchingAsync(CancellationToken.None);
        Assert.Single(watching);
        Assert.Equal(firstComment, watching[0].LastQualifyingCommentAt);
    }

    // ── 2. Genuinely newer comment advances the quiet period ──────

    [Fact]
    public async Task Upsert_NewerQualifyingComment_AdvancesQuietPeriodAndRefreshesPayload()
    {
        // Arrange: seed a Watching row.
        var firstComment = DateTimeOffset.UtcNow.AddMinutes(-10);
        await _store!.UpsertAsync(
            RevivalRequest("pr-3", "bundle-3", "[\"v1\"]", 1, firstComment, firstComment),
            CancellationToken.None);

        // Act: replay with a genuinely newer qualifying comment and a richer bundle.
        var newerComment = DateTimeOffset.UtcNow.AddMinutes(-2);
        var advanced = await _store!.UpsertAsync(
            RevivalRequest("pr-3", "bundle-3", "[\"v1\",\"reply\"]", 2, firstComment, newerComment),
            CancellationToken.None);

        // Assert: quiet period advances, earliest anchor is preserved, and the
        // bundle payload is refreshed. UpdatedAt is bumped.
        Assert.Equal(newerComment, advanced.LastQualifyingCommentAt);
        Assert.Equal(firstComment, advanced.FirstQualifyingCommentAt);
        Assert.Equal("[\"v1\",\"reply\"]", advanced.FeedbackBundleJson);
        Assert.Equal(2, advanced.ThreadCount);
        Assert.Equal(ReworkFeedbackStatus.Watching, advanced.Status);
        Assert.True(advanced.UpdatedAt > firstComment);
    }

    // ── 3. Non-regression of advanced lifecycle states ────────────

    [Fact]
    public async Task Upsert_WatchingReplay_DoesNotRegressSoakedRow()
    {
        // Arrange: soak the row.
        var lastComment = DateTimeOffset.UtcNow.AddMinutes(-10);
        var row = await _store!.UpsertAsync(
            RevivalRequest("pr-4", "bundle-4", "[]", 1, lastComment, lastComment),
            CancellationToken.None);

        var soaked = await _store!.MarkSoakedAsync(row.Id, CancellationToken.None);
        Assert.NotNull(soaked);
        var soakedUpdatedAt = soaked!.UpdatedAt;
        await Task.Delay(20);

        // Act: a later poll replays the same bundle as a fresh Watching observation.
        var replayed = await _store!.UpsertAsync(
            RevivalRequest("pr-4", "bundle-4", "[\"changed\"]", 9, lastComment, lastComment),
            CancellationToken.None);

        // Assert: Soaked is preserved, the soak baseline is untouched, and
        // UpdatedAt is not bumped by the replay.
        Assert.Equal(row.Id, replayed.Id);
        Assert.Equal(ReworkFeedbackStatus.Soaked, replayed.Status);
        Assert.Equal(lastComment, replayed.LastQualifyingCommentAt);
        Assert.Equal(soakedUpdatedAt, replayed.UpdatedAt);

        var entity = await GetEntityAsync(row.Id);
        Assert.Equal((int)ReworkFeedbackStatus.Soaked, entity.Status);
        Assert.Equal(soakedUpdatedAt, entity.UpdatedAt);
        Assert.Equal(1, entity.ThreadCount);

        // The replayed Soaked row is still eligible for materialization.
        var soakedRows = await _store!.GetSoakedAsync(CancellationToken.None);
        Assert.Single(soakedRows);
    }

    [Fact]
    public async Task Upsert_WatchingReplay_DoesNotRegressMaterializedRow()
    {
        // Arrange: progress the row through to Materialized.
        var lastComment = DateTimeOffset.UtcNow.AddMinutes(-10);
        var row = await _store!.UpsertAsync(
            RevivalRequest("pr-5", "bundle-5", "[]", 1, lastComment, lastComment),
            CancellationToken.None);

        await _store!.MarkSoakedAsync(row.Id, CancellationToken.None);
        await _store!.MarkMaterializedAsync(row.Id, CancellationToken.None);

        var materialized = await GetEntityAsync(row.Id);
        var materializedUpdatedAt = materialized.UpdatedAt;
        await Task.Delay(20);

        // Act: a later poll replays the same bundle as a fresh Watching observation.
        var replayed = await _store!.UpsertAsync(
            RevivalRequest("pr-5", "bundle-5", "[\"changed\"]", 9, lastComment, lastComment),
            CancellationToken.None);

        // Assert: Materialized is preserved — the bundle is not re-watched or
        // re-materialized, and timestamps are untouched.
        Assert.Equal(ReworkFeedbackStatus.Materialized, replayed.Status);
        Assert.Equal(lastComment, replayed.LastQualifyingCommentAt);
        Assert.Equal(materializedUpdatedAt, replayed.UpdatedAt);

        var entity = await GetEntityAsync(row.Id);
        Assert.Equal((int)ReworkFeedbackStatus.Materialized, entity.Status);
        Assert.Equal(1, entity.ThreadCount);

        Assert.Empty(await _store!.GetWatchingAsync(CancellationToken.None));
        Assert.Empty(await _store!.GetSoakedAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Upsert_WatchingReplay_DoesNotRegressSupersededRow()
    {
        // Arrange: supersede the row (e.g. a newer bundle took over).
        var lastComment = DateTimeOffset.UtcNow.AddMinutes(-10);
        var row = await _store!.UpsertAsync(
            RevivalRequest("pr-6", "bundle-6", "[]", 1, lastComment, lastComment),
            CancellationToken.None);

        await _store!.MarkSupersededAsync(row.Id, CancellationToken.None);
        var superseded = await GetEntityAsync(row.Id);
        var supersededUpdatedAt = superseded.UpdatedAt;
        await Task.Delay(20);

        // Act: a later poll replays the superseded bundle as a fresh Watching
        // observation (it should not come back to life).
        var replayed = await _store!.UpsertAsync(
            RevivalRequest("pr-6", "bundle-6", "[\"changed\"]", 9, lastComment, lastComment),
            CancellationToken.None);

        // Assert: Superseded is preserved and the row stays out of Watching/Soaked.
        Assert.Equal(ReworkFeedbackStatus.Superseded, replayed.Status);
        Assert.Equal(supersededUpdatedAt, replayed.UpdatedAt);

        Assert.Empty(await _store!.GetWatchingAsync(CancellationToken.None));
        Assert.Empty(await _store!.GetSoakedAsync(CancellationToken.None));

        var entity = await GetEntityAsync(row.Id);
        Assert.Equal((int)ReworkFeedbackStatus.Superseded, entity.Status);
    }

    [Fact]
    public async Task Upsert_NewerComment_OnWatchingRow_StillSoaksAndMaterializesOnce()
    {
        // End-to-end check that monotonic replay does not block the normal
        // lifecycle: a Watching row that receives a newer comment still soaks
        // and reaches Materialized exactly once.
        var firstComment = DateTimeOffset.UtcNow.AddMinutes(-10);
        var row = await _store!.UpsertAsync(
            RevivalRequest("pr-7", "bundle-7", "[]", 1, firstComment, firstComment),
            CancellationToken.None);

        // Newer comment refreshes the quiet-period anchor, then threshold elapses.
        var newerComment = DateTimeOffset.UtcNow.AddMinutes(-1);
        await _store!.UpsertAsync(
            RevivalRequest("pr-7", "bundle-7", "[]", 1, firstComment, newerComment),
            CancellationToken.None);

        var soaked = await _store!.MarkSoakedAsync(row.Id, CancellationToken.None);
        Assert.NotNull(soaked);
        Assert.Equal(ReworkFeedbackStatus.Soaked, soaked!.Status);

        await _store!.MarkMaterializedAsync(row.Id, CancellationToken.None);
        var entity = await GetEntityAsync(row.Id);
        Assert.Equal((int)ReworkFeedbackStatus.Materialized, entity.Status);

        // A subsequent Watching replay against the Materialized row is a no-op.
        var replayed = await _store!.UpsertAsync(
            RevivalRequest("pr-7", "bundle-7", "[]", 1, firstComment, firstComment),
            CancellationToken.None);
        Assert.Equal(ReworkFeedbackStatus.Materialized, replayed.Status);
        Assert.Single(await _db!.ReworkFeedback.ToListAsync());
    }
}

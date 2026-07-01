using AgentController.Application;
using AgentController.Domain;
using AgentController.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace AgentController.Infrastructure.Data.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IReworkFeedbackStore"/> using SQLite.
/// Supports upsert, list watching, and status transitions (Soaked/Superseded)
/// for the feedback soak-window debounce state machine.
/// </summary>
internal sealed class EfReworkFeedbackStore : IReworkFeedbackStore
{
    private readonly AgentControllerDbContext _db;

    public EfReworkFeedbackStore(AgentControllerDbContext db)
    {
        _db = db;
    }

    public async Task<ReworkFeedback> UpsertAsync(
        string originatingRunId,
        string pullRequestId,
        string feedbackBundleId,
        string feedbackBundleJson,
        int threadCount,
        DateTimeOffset firstQualifyingCommentAt,
        DateTimeOffset lastQualifyingCommentAt,
        ReworkFeedbackStatus status,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        // Try to find existing row by the unique composite key.
        var existing = await _db.ReworkFeedback
            .FirstOrDefaultAsync(
                e => e.PullRequestId == pullRequestId && e.FeedbackBundleId == feedbackBundleId,
                cancellationToken);

        if (existing is not null)
        {
            // Update existing row.
            existing.OriginatingRunId = originatingRunId;
            existing.FeedbackBundleJson = feedbackBundleJson;
            existing.ThreadCount = threadCount;
            existing.FirstQualifyingCommentAt = firstQualifyingCommentAt;
            existing.LastQualifyingCommentAt = lastQualifyingCommentAt;
            existing.Status = (int)status;
            existing.UpdatedAt = now;

            await _db.SaveChangesAsync(cancellationToken);
            return MapToDomain(existing);
        }

        // Insert new row.
        var entity = new ReworkFeedbackEntity
        {
            Id = GenerateId("rfeedback"),
            OriginatingRunId = originatingRunId,
            PullRequestId = pullRequestId,
            FeedbackBundleId = feedbackBundleId,
            FeedbackBundleJson = feedbackBundleJson,
            ThreadCount = threadCount,
            FirstQualifyingCommentAt = firstQualifyingCommentAt,
            LastQualifyingCommentAt = lastQualifyingCommentAt,
            Status = (int)status,
            CreatedAt = now,
            UpdatedAt = now,
        };

        _db.ReworkFeedback.Add(entity);
        await _db.SaveChangesAsync(cancellationToken);

        return MapToDomain(entity);
    }

    public async Task<IReadOnlyList<ReworkFeedback>> GetWatchingAsync(
        CancellationToken cancellationToken)
    {
        var entities = await _db.ReworkFeedback
            .Where(e => e.Status == (int)ReworkFeedbackStatus.Watching)
            .ToListAsync(cancellationToken);

        return entities
            .OrderBy(e => e.LastQualifyingCommentAt)
            .Select(MapToDomain)
            .ToList();
    }

    public async Task<ReworkFeedback?> MarkSoakedAsync(
        string id,
        CancellationToken cancellationToken)
    {
        var entity = await _db.ReworkFeedback
            .FindAsync([id], cancellationToken);

        if (entity is null)
            return null;

        // Idempotent guard: only transition from Watching.
        if (entity.Status != (int)ReworkFeedbackStatus.Watching)
            return null;

        entity.Status = (int)ReworkFeedbackStatus.Soaked;
        entity.UpdatedAt = DateTimeOffset.UtcNow;

        await _db.SaveChangesAsync(cancellationToken);
        return MapToDomain(entity);
    }

    public async Task MarkSupersededAsync(
        string id,
        CancellationToken cancellationToken)
    {
        var entity = await _db.ReworkFeedback
            .FindAsync([id], cancellationToken);

        if (entity is null)
            return;

        // Idempotent: no-op if already Superseded or Soaked.
        if (entity.Status == (int)ReworkFeedbackStatus.Superseded ||
            entity.Status == (int)ReworkFeedbackStatus.Soaked)
            return;

        entity.Status = (int)ReworkFeedbackStatus.Superseded;
        entity.UpdatedAt = DateTimeOffset.UtcNow;

        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ReworkFeedback>> GetSoakedAsync(
        CancellationToken cancellationToken)
    {
        var entities = await _db.ReworkFeedback
            .Where(e => e.Status == (int)ReworkFeedbackStatus.Soaked)
            .ToListAsync(cancellationToken);

        return entities
            .OrderBy(e => e.UpdatedAt)
            .Select(MapToDomain)
            .ToList();
    }

    public async Task MarkMaterializedAsync(
        string id,
        CancellationToken cancellationToken)
    {
        var entity = await _db.ReworkFeedback
            .FindAsync([id], cancellationToken);

        if (entity is null)
            return;

        // Only transition from Soaked — prevents re-processing.
        if (entity.Status != (int)ReworkFeedbackStatus.Soaked)
            return;

        entity.Status = (int)ReworkFeedbackStatus.Materialized;
        entity.UpdatedAt = DateTimeOffset.UtcNow;

        await _db.SaveChangesAsync(cancellationToken);
    }

    private static ReworkFeedback MapToDomain(ReworkFeedbackEntity entity)
    {
        return new ReworkFeedback
        {
            Id = entity.Id,
            OriginatingRunId = entity.OriginatingRunId,
            PullRequestId = entity.PullRequestId,
            FeedbackBundleId = entity.FeedbackBundleId,
            FeedbackBundleJson = entity.FeedbackBundleJson,
            FirstQualifyingCommentAt = entity.FirstQualifyingCommentAt,
            LastQualifyingCommentAt = entity.LastQualifyingCommentAt,
            ThreadCount = entity.ThreadCount,
            Status = (ReworkFeedbackStatus)entity.Status,
            CreatedAt = entity.CreatedAt,
            UpdatedAt = entity.UpdatedAt,
        };
    }

    private static string GenerateId(string prefix)
    {
        return $"{prefix}_{Guid.NewGuid():N}";
    }
}

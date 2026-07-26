using AgentController.Application;
using AgentController.Domain;
using AgentController.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace AgentController.Infrastructure.Data.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IReworkFeedbackStore"/> using SQLite.
/// Supports restart-safe feedback observation, assistance-story receipts, and
/// status transitions for the feedback soak-window state machine.
/// </summary>
internal sealed class EfReworkFeedbackStore : IReworkFeedbackStore
{
    private readonly AgentControllerDbContext _db;

    public EfReworkFeedbackStore(AgentControllerDbContext db)
    {
        _db = db;
    }

    public Task<ReworkFeedback> UpsertAsync(
        string? originatingRunId,
        string pullRequestId,
        string feedbackBundleId,
        string feedbackBundleJson,
        int threadCount,
        DateTimeOffset firstQualifyingCommentAt,
        DateTimeOffset lastQualifyingCommentAt,
        ReworkFeedbackStatus status,
        CancellationToken cancellationToken)
    {
        return UpsertAsync(
            new ReworkFeedbackUpsertRequest
            {
                RequestMode = ReworkRequestMode.Revival,
                PullRequest = new PullRequestReference { PullRequestId = pullRequestId },
                OriginatingRunId = originatingRunId,
                FeedbackBundleId = feedbackBundleId,
                FeedbackBundleJson = feedbackBundleJson,
                ThreadCount = threadCount,
                FirstQualifyingCommentAt = firstQualifyingCommentAt,
                LastQualifyingCommentAt = lastQualifyingCommentAt,
                Status = status,
            },
            cancellationToken);
    }

    public async Task<ReworkFeedback> UpsertAsync(
        ReworkFeedbackUpsertRequest request,
        CancellationToken cancellationToken)
    {
        Validate(request);

        var canonicalKey = NullIfEmpty(request.PullRequest.CanonicalKey);
        var existing = await FindExistingAsync(request, canonicalKey, cancellationToken);
        if (existing is not null)
        {
            ApplyObservation(existing, request, canonicalKey, DateTimeOffset.UtcNow);
            await _db.SaveChangesAsync(cancellationToken);
            return MapToDomain(existing);
        }

        var now = DateTimeOffset.UtcNow;
        var entity = new ReworkFeedbackEntity
        {
            Id = GenerateId("rfeedback"),
            CreatedAt = now,
        };
        ApplyObservation(entity, request, canonicalKey, now);
        _db.ReworkFeedback.Add(entity);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            return MapToDomain(entity);
        }
        catch (DbUpdateException)
        {
            // Another worker can observe the same PR between the read and insert.
            // Detach the failed insert, then return/update the row protected by the
            // canonical unique key. Unrelated constraint failures still propagate.
            _db.Entry(entity).State = EntityState.Detached;
            var concurrent = await FindExistingAsync(request, canonicalKey, cancellationToken);
            if (concurrent is null)
                throw;

            ApplyObservation(concurrent, request, canonicalKey, DateTimeOffset.UtcNow);
            await _db.SaveChangesAsync(cancellationToken);
            return MapToDomain(concurrent);
        }
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

    public async Task<IReadOnlyList<ReworkFeedback>> GetTrackedAsync(
        CancellationToken cancellationToken)
    {
        var entities = await _db.ReworkFeedback
            .AsNoTracking()
            .Where(e => e.Status != (int)ReworkFeedbackStatus.Superseded)
            .ToListAsync(cancellationToken);

        return entities
            .OrderBy(e => e.CreatedAt)
            .Select(MapToDomain)
            .ToList();
    }

    public async Task<ReworkFeedback?> GetByCorrelationIdAsync(
        string correlationId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(correlationId))
            return null;

        var entity = await _db.ReworkFeedback
            .FirstOrDefaultAsync(e => e.CorrelationId == correlationId, cancellationToken);
        return entity is null ? null : MapToDomain(entity);
    }

    public async Task<ReworkFeedback> RecordAssistanceStoryAsync(
        string id,
        AssistanceStoryReceipt receipt,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(receipt.CorrelationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(receipt.ExternalId);

        var entity = await _db.ReworkFeedback.FindAsync([id], cancellationToken)
            ?? throw new KeyNotFoundException($"Rework feedback '{id}' was not found.");

        if (entity.RequestMode != (int)ReworkRequestMode.Assistance)
        {
            throw new InvalidOperationException(
                "Assistance story receipts can only be recorded for Assistance feedback.");
        }

        entity.CorrelationId = MergeReceiptValue(
            entity.CorrelationId,
            receipt.CorrelationId,
            nameof(receipt.CorrelationId));
        entity.AssistanceStoryExternalId = MergeReceiptValue(
            entity.AssistanceStoryExternalId,
            receipt.ExternalId,
            nameof(receipt.ExternalId));
        entity.AssistanceStoryWorkItemId = MergeReceiptValue(
            entity.AssistanceStoryWorkItemId,
            receipt.WorkItemId,
            nameof(receipt.WorkItemId));
        entity.AssistanceStoryUrl = MergeReceiptValue(
            entity.AssistanceStoryUrl,
            receipt.Url,
            nameof(receipt.Url));
        entity.UpdatedAt = DateTimeOffset.UtcNow;

        await _db.SaveChangesAsync(cancellationToken);
        return MapToDomain(entity);
    }

    public async Task<ReworkFeedback?> MarkSoakedAsync(
        string id,
        CancellationToken cancellationToken)
    {
        var entity = await _db.ReworkFeedback.FindAsync([id], cancellationToken);
        if (entity is null || entity.Status != (int)ReworkFeedbackStatus.Watching)
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
        var entity = await _db.ReworkFeedback.FindAsync([id], cancellationToken);
        if (entity is null)
            return;

        if (entity.Status is (int)ReworkFeedbackStatus.Superseded
            or (int)ReworkFeedbackStatus.Soaked)
        {
            return;
        }

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
        var entity = await _db.ReworkFeedback.FindAsync([id], cancellationToken);
        if (entity is null || entity.Status != (int)ReworkFeedbackStatus.Soaked)
            return;

        entity.Status = (int)ReworkFeedbackStatus.Materialized;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task<ReworkFeedbackEntity?> FindExistingAsync(
        ReworkFeedbackUpsertRequest request,
        string? canonicalKey,
        CancellationToken cancellationToken)
    {
        var requestMode = (int)request.RequestMode;
        if (canonicalKey is not null)
        {
            return await _db.ReworkFeedback.FirstOrDefaultAsync(
                entity => entity.RequestMode == requestMode
                    && entity.CanonicalPullRequestKey == canonicalKey
                    && entity.FeedbackBundleId == request.FeedbackBundleId,
                cancellationToken);
        }

        return await _db.ReworkFeedback.FirstOrDefaultAsync(
            entity => entity.RequestMode == requestMode
                && entity.CanonicalPullRequestKey == null
                && entity.PullRequestId == request.PullRequest.PullRequestId
                && entity.FeedbackBundleId == request.FeedbackBundleId,
            cancellationToken);
    }

    private static void ApplyObservation(
        ReworkFeedbackEntity entity,
        ReworkFeedbackUpsertRequest request,
        string? canonicalKey,
        DateTimeOffset updatedAt)
    {
        entity.RequestMode = (int)request.RequestMode;
        entity.CanonicalPullRequestKey = canonicalKey;
        entity.PullRequestEnvironmentKey = NullIfEmpty(request.PullRequest.EnvironmentKey);
        entity.PullRequestRepositoryKey = NullIfEmpty(request.PullRequest.RepositoryKey);
        entity.PullRequestId = request.PullRequest.PullRequestId;
        entity.PullRequestUrl = NullIfEmpty(request.PullRequest.PullRequestUrl);
        entity.PullRequestSourceBranch = NullIfEmpty(request.PullRequest.SourceBranch);
        entity.PullRequestTargetBranch = NullIfEmpty(request.PullRequest.TargetBranch);
        entity.PullRequestSourceCommitSha = NullIfEmpty(request.PullRequest.SourceCommitSha);
        entity.OriginatingRunId = NullIfEmpty(request.OriginatingRunId);
        entity.FeedbackBundleId = request.FeedbackBundleId;
        entity.FeedbackBundleJson = request.FeedbackBundleJson;
        entity.ThreadCount = request.ThreadCount;
        entity.FirstQualifyingCommentAt = request.FirstQualifyingCommentAt;
        entity.LastQualifyingCommentAt = request.LastQualifyingCommentAt;
        entity.Status = (int)request.Status;
        entity.CorrelationId = MergeReceiptValue(
            entity.CorrelationId,
            request.CorrelationId,
            nameof(request.CorrelationId));
        entity.UpdatedAt = updatedAt;
    }

    private static void Validate(ReworkFeedbackUpsertRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.PullRequest);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.PullRequest.PullRequestId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.FeedbackBundleId);

        if (request.RequestMode == ReworkRequestMode.Revival
            && string.IsNullOrWhiteSpace(request.OriginatingRunId))
        {
            throw new ArgumentException(
                "Revival feedback requires an originating run.",
                nameof(request));
        }

        if (request.RequestMode == ReworkRequestMode.Assistance)
        {
            if (!request.PullRequest.HasCanonicalIdentity)
            {
                throw new ArgumentException(
                    "Assistance feedback requires a canonical pull-request identity.",
                    nameof(request));
            }

            if (string.IsNullOrWhiteSpace(request.CorrelationId))
            {
                throw new ArgumentException(
                    "Assistance feedback requires a materialization correlation ID.",
                    nameof(request));
            }
        }
    }

    private static ReworkFeedback MapToDomain(ReworkFeedbackEntity entity)
    {
        var pullRequest = new PullRequestReference
        {
            EnvironmentKey = entity.PullRequestEnvironmentKey ?? string.Empty,
            RepositoryKey = entity.PullRequestRepositoryKey ?? string.Empty,
            PullRequestId = entity.PullRequestId,
            PullRequestUrl = entity.PullRequestUrl ?? string.Empty,
            SourceBranch = entity.PullRequestSourceBranch ?? string.Empty,
            TargetBranch = entity.PullRequestTargetBranch ?? string.Empty,
            SourceCommitSha = entity.PullRequestSourceCommitSha ?? string.Empty,
        };

        return new ReworkFeedback
        {
            Id = entity.Id,
            RequestMode = (ReworkRequestMode)entity.RequestMode,
            PullRequest = pullRequest,
            OriginatingRunId = entity.OriginatingRunId,
            PullRequestId = entity.PullRequestId,
            FeedbackBundleId = entity.FeedbackBundleId,
            FeedbackBundleJson = entity.FeedbackBundleJson,
            FirstQualifyingCommentAt = entity.FirstQualifyingCommentAt,
            LastQualifyingCommentAt = entity.LastQualifyingCommentAt,
            ThreadCount = entity.ThreadCount,
            CorrelationId = entity.CorrelationId,
            AssistanceStoryWorkItemId = entity.AssistanceStoryWorkItemId,
            AssistanceStoryExternalId = entity.AssistanceStoryExternalId,
            AssistanceStoryUrl = entity.AssistanceStoryUrl,
            Status = (ReworkFeedbackStatus)entity.Status,
            CreatedAt = entity.CreatedAt,
            UpdatedAt = entity.UpdatedAt,
        };
    }

    private static string? MergeReceiptValue(
        string? current,
        string? incoming,
        string fieldName)
    {
        incoming = NullIfEmpty(incoming);
        if (incoming is null)
            return current;
        if (string.IsNullOrWhiteSpace(current))
            return incoming;
        if (string.Equals(current, incoming, StringComparison.Ordinal))
            return current;

        throw new InvalidOperationException(
            $"Assistance materialization field '{fieldName}' is already recorded with a different value.");
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static string GenerateId(string prefix) => $"{prefix}_{Guid.NewGuid():N}";
}

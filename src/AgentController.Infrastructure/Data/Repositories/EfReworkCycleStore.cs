using AgentController.Application;
using AgentController.Domain;
using AgentController.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace AgentController.Infrastructure.Data.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IReworkCycleStore"/> using SQLite.
/// Supports restart-safe materialization of Pending cycles and consumption at claim time.
/// </summary>
internal sealed class EfReworkCycleStore : IReworkCycleStore
{
    private readonly AgentControllerDbContext _db;

    public EfReworkCycleStore(AgentControllerDbContext db)
    {
        _db = db;
    }

    public async Task<ReworkCycle?> GetPendingForWorkItemAsync(
        string workItemId,
        CancellationToken cancellationToken)
    {
        var entity = await _db.ReworkCycles
            .Where(e => e.WorkItemId == workItemId && e.Status == (int)ReworkCycleStatus.Pending)
            .OrderBy(e => e.CycleNumber)
            .FirstOrDefaultAsync(cancellationToken);

        return entity is null ? null : MapToDomain(entity);
    }

    public async Task<ReworkCycle?> GetConsumedByRunIdAsync(
        string runId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(runId))
            return null;

        var entity = await _db.ReworkCycles
            .AsNoTracking()
            .FirstOrDefaultAsync(
                cycle => cycle.Status == (int)ReworkCycleStatus.Consumed
                    && cycle.NewRunId == runId,
                cancellationToken);

        return entity is null ? null : MapToDomain(entity);
    }

    public Task<bool> ExistsByFeedbackBundleIdAsync(
        string feedbackBundleId,
        CancellationToken cancellationToken)
    {
        return _db.ReworkCycles
            .AnyAsync(e => e.FeedbackBundleId == feedbackBundleId, cancellationToken);
    }

    public Task<bool> ExistsAsync(
        ReworkRequestMode requestMode,
        PullRequestReference pullRequest,
        string feedbackBundleId,
        CancellationToken cancellationToken)
    {
        var canonicalKey = NullIfEmpty(pullRequest.CanonicalKey);
        if (canonicalKey is null)
            return ExistsByFeedbackBundleIdAsync(feedbackBundleId, cancellationToken);

        var persistedMode = (int)requestMode;
        return _db.ReworkCycles.AnyAsync(
            entity => entity.RequestMode == persistedMode
                && entity.CanonicalPullRequestKey == canonicalKey
                && entity.FeedbackBundleId == feedbackBundleId,
            cancellationToken);
    }

    public async Task<ReworkCycle?> GetByCorrelationIdAsync(
        string correlationId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(correlationId))
            return null;

        var entity = await _db.ReworkCycles
            .FirstOrDefaultAsync(e => e.CorrelationId == correlationId, cancellationToken);
        return entity is null ? null : MapToDomain(entity);
    }

    public async Task<ReworkCycle> CreateAsync(
        ReworkCycleCreateRequest request,
        CancellationToken cancellationToken)
    {
        Validate(request);
        var canonicalKey = NullIfEmpty(request.PullRequest.CanonicalKey);

        if (request.RequestMode == ReworkRequestMode.Assistance)
        {
            return await CreateAssistanceAsync(request, canonicalKey!, cancellationToken);
        }

        return await CreateWithProvidedCycleNumberAsync(
            request,
            canonicalKey,
            cancellationToken);
    }

    public async Task<ReworkCycle> CreateAsync(
        string workItemId,
        int cycleNumber,
        string? priorRunId,
        string branchName,
        string pullRequestUrl,
        string baseCommitSha,
        string feedbackBundleJson,
        string feedbackBundleId,
        CancellationToken cancellationToken)
    {
        var request = new ReworkCycleCreateRequest
        {
            RequestMode = ReworkRequestMode.Revival,
            PullRequest = new PullRequestReference
            {
                PullRequestUrl = pullRequestUrl,
                SourceBranch = branchName,
                SourceCommitSha = baseCommitSha,
            },
            WorkItemId = workItemId,
            CycleNumber = cycleNumber,
            PriorRunId = priorRunId,
            BranchName = branchName,
            PullRequestUrl = pullRequestUrl,
            BaseCommitSha = baseCommitSha,
            FeedbackBundleJson = feedbackBundleJson,
            FeedbackBundleId = feedbackBundleId,
        };
        Validate(request);

        // Preserve the historical contract: duplicate legacy bundle IDs surface
        // the database uniqueness failure rather than returning an existing row.
        var entity = CreateEntity(request, canonicalKey: null);
        _db.ReworkCycles.Add(entity);
        await _db.SaveChangesAsync(cancellationToken);
        return MapToDomain(entity);
    }

    public async Task MarkConsumedAsync(
        string id,
        string newRunId,
        CancellationToken cancellationToken)
    {
        var entity = await _db.ReworkCycles.FindAsync([id], cancellationToken);
        if (entity is null || entity.Status == (int)ReworkCycleStatus.Consumed)
            return;

        entity.Status = (int)ReworkCycleStatus.Consumed;
        entity.ConsumedAt = DateTimeOffset.UtcNow;
        entity.NewRunId = newRunId;
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ReworkCycle>> ListPendingAsync(
        CancellationToken cancellationToken)
    {
        // SQLite cannot translate DateTimeOffset ORDER BY clauses, so sort client-side.
        var entities = await _db.ReworkCycles
            .Where(e => e.Status == (int)ReworkCycleStatus.Pending)
            .ToListAsync(cancellationToken);

        return entities.OrderBy(e => e.CreatedAt).Select(MapToDomain).ToList();
    }

    public async Task<IReadOnlyList<ReworkCycle>> ListConsumedAsync(
        CancellationToken cancellationToken)
    {
        var entities = await _db.ReworkCycles
            .Where(e => e.Status == (int)ReworkCycleStatus.Consumed)
            .ToListAsync(cancellationToken);

        return entities.Select(MapToDomain).ToList();
    }

    public async Task<int> GetMaxCycleNumberAsync(
        string workItemId,
        CancellationToken cancellationToken)
    {
        var maxCycle = await _db.ReworkCycles
            .Where(e => e.WorkItemId == workItemId)
            .MaxAsync(e => (int?)e.CycleNumber, cancellationToken);

        return maxCycle ?? 0;
    }

    public Task<int> GetMaxAssistanceCycleNumberAsync(
        PullRequestReference pullRequest,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pullRequest);
        if (!pullRequest.HasCanonicalIdentity)
        {
            throw new ArgumentException(
                "Assistance cycle numbering requires a canonical pull-request identity.",
                nameof(pullRequest));
        }

        return GetMaxAssistanceCycleNumberCoreAsync(
            pullRequest.CanonicalKey,
            cancellationToken);
    }

    public async Task MarkReactivatedAsync(
        string id,
        CancellationToken cancellationToken)
    {
        var entity = await _db.ReworkCycles.FindAsync([id], cancellationToken);
        if (entity is null || entity.ReactivatedAt is not null)
            return;

        entity.ReactivatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task<ReworkCycle> CreateWithProvidedCycleNumberAsync(
        ReworkCycleCreateRequest request,
        string? canonicalKey,
        CancellationToken cancellationToken)
    {
        var existing = await FindExistingAsync(request, canonicalKey, cancellationToken);
        if (existing is not null)
        {
            EnsureSameMaterialization(existing, request, canonicalKey);
            return MapToDomain(existing);
        }

        var entity = CreateEntity(request, canonicalKey);
        _db.ReworkCycles.Add(entity);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            return MapToDomain(entity);
        }
        catch (DbUpdateException)
        {
            // A concurrent worker may have crossed the read/insert boundary.
            // Resolve the winner by its correlation/per-PR bundle guard.
            _db.Entry(entity).State = EntityState.Detached;
            var concurrent = await FindExistingAsync(request, canonicalKey, cancellationToken);
            if (concurrent is null)
                throw;

            EnsureSameMaterialization(concurrent, request, canonicalKey);
            return MapToDomain(concurrent);
        }
    }

    private async Task<ReworkCycle> CreateAssistanceAsync(
        ReworkCycleCreateRequest request,
        string canonicalKey,
        CancellationToken cancellationToken)
    {
        var existing = await FindExistingAsync(request, canonicalKey, cancellationToken);
        if (existing is not null)
        {
            EnsureSameMaterialization(existing, request, canonicalKey);
            return MapToDomain(existing);
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var maxCycleNumber = await GetMaxAssistanceCycleNumberCoreAsync(
                canonicalKey,
                cancellationToken);
            if (maxCycleNumber == int.MaxValue)
            {
                throw new InvalidOperationException(
                    $"Pull request '{canonicalKey}' has exhausted Assistance cycle numbers.");
            }

            var numberedRequest = request with { CycleNumber = maxCycleNumber + 1 };
            var entity = CreateEntity(numberedRequest, canonicalKey);
            _db.ReworkCycles.Add(entity);

            try
            {
                await _db.SaveChangesAsync(cancellationToken);
                return MapToDomain(entity);
            }
            catch (DbUpdateException)
            {
                _db.Entry(entity).State = EntityState.Detached;

                // The same materialization may have won the race. In that case,
                // return it instead of allocating another cycle.
                var concurrent = await FindExistingAsync(
                    request,
                    canonicalKey,
                    cancellationToken);
                if (concurrent is not null)
                {
                    EnsureSameMaterialization(concurrent, request, canonicalKey);
                    return MapToDomain(concurrent);
                }

                // A different Assistance request may have claimed this cycle number
                // after the max query. The unique per-PR cycle index is the arbiter;
                // retry only that conflict and let unrelated persistence errors escape.
                var cycleNumberWasClaimed = await _db.ReworkCycles
                    .AsNoTracking()
                    .AnyAsync(
                        candidate => candidate.RequestMode == (int)ReworkRequestMode.Assistance
                            && candidate.CanonicalPullRequestKey == canonicalKey
                            && candidate.CycleNumber == numberedRequest.CycleNumber,
                        cancellationToken);
                if (!cycleNumberWasClaimed)
                    throw;
            }
        }
    }

    private async Task<int> GetMaxAssistanceCycleNumberCoreAsync(
        string canonicalKey,
        CancellationToken cancellationToken)
    {
        var maxCycle = await _db.ReworkCycles
            .Where(entity => entity.RequestMode == (int)ReworkRequestMode.Assistance
                && entity.CanonicalPullRequestKey == canonicalKey)
            .MaxAsync(entity => (int?)entity.CycleNumber, cancellationToken);

        return maxCycle ?? 0;
    }

    private async Task<ReworkCycleEntity?> FindExistingAsync(
        ReworkCycleCreateRequest request,
        string? canonicalKey,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(request.CorrelationId))
        {
            var byCorrelation = await _db.ReworkCycles.FirstOrDefaultAsync(
                entity => entity.CorrelationId == request.CorrelationId,
                cancellationToken);
            if (byCorrelation is not null)
                return byCorrelation;
        }

        if (canonicalKey is not null)
        {
            var requestMode = (int)request.RequestMode;
            return await _db.ReworkCycles.FirstOrDefaultAsync(
                entity => entity.RequestMode == requestMode
                    && entity.CanonicalPullRequestKey == canonicalKey
                    && entity.FeedbackBundleId == request.FeedbackBundleId,
                cancellationToken);
        }

        return await _db.ReworkCycles.FirstOrDefaultAsync(
            entity => entity.CanonicalPullRequestKey == null
                && entity.FeedbackBundleId == request.FeedbackBundleId,
            cancellationToken);
    }

    private static ReworkCycleEntity CreateEntity(
        ReworkCycleCreateRequest request,
        string? canonicalKey)
    {
        return new ReworkCycleEntity
        {
            Id = GenerateId("rcycle"),
            RequestMode = (int)request.RequestMode,
            CanonicalPullRequestKey = canonicalKey,
            PullRequestEnvironmentKey = NullIfEmpty(request.PullRequest.EnvironmentKey),
            PullRequestRepositoryKey = NullIfEmpty(request.PullRequest.RepositoryKey),
            PullRequestId = NullIfEmpty(request.PullRequest.PullRequestId),
            PullRequestTargetBranch = NullIfEmpty(request.PullRequest.TargetBranch),
            WorkItemId = request.WorkItemId,
            CycleNumber = request.CycleNumber,
            PriorRunId = NullIfEmpty(request.PriorRunId),
            BranchName = FirstNonEmpty(request.BranchName, request.PullRequest.SourceBranch),
            PullRequestUrl = FirstNonEmpty(
                request.PullRequestUrl,
                request.PullRequest.PullRequestUrl),
            BaseCommitSha = FirstNonEmpty(
                request.BaseCommitSha,
                request.PullRequest.SourceCommitSha),
            FeedbackBundleJson = request.FeedbackBundleJson,
            FeedbackBundleId = request.FeedbackBundleId,
            CorrelationId = NullIfEmpty(request.CorrelationId),
            Status = (int)ReworkCycleStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
        };
    }

    private static void EnsureSameMaterialization(
        ReworkCycleEntity entity,
        ReworkCycleCreateRequest request,
        string? canonicalKey)
    {
        if (entity.RequestMode != (int)request.RequestMode
            || !string.Equals(
                entity.CanonicalPullRequestKey,
                canonicalKey,
                StringComparison.Ordinal)
            || !string.Equals(
                entity.FeedbackBundleId,
                request.FeedbackBundleId,
                StringComparison.Ordinal)
            || !string.Equals(entity.WorkItemId, request.WorkItemId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The materialization correlation is already associated with a different cycle.");
        }
    }

    private static void Validate(ReworkCycleCreateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.PullRequest);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkItemId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.FeedbackBundleId);

        if (request.RequestMode == ReworkRequestMode.Revival)
        {
            if (request.CycleNumber < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(request),
                    "Revival cycle number must be positive.");
            }

            if (string.IsNullOrWhiteSpace(request.PriorRunId))
            {
                throw new ArgumentException(
                    "Revival cycles require a prior run.",
                    nameof(request));
            }
        }

        if (request.RequestMode == ReworkRequestMode.Assistance)
        {
            if (!request.PullRequest.HasCanonicalIdentity)
            {
                throw new ArgumentException(
                    "Assistance cycles require a canonical pull-request identity.",
                    nameof(request));
            }

            if (string.IsNullOrWhiteSpace(request.CorrelationId))
            {
                throw new ArgumentException(
                    "Assistance cycles require a materialization correlation ID.",
                    nameof(request));
            }
        }
    }

    private static ReworkCycle MapToDomain(ReworkCycleEntity entity)
    {
        var pullRequest = new PullRequestReference
        {
            EnvironmentKey = entity.PullRequestEnvironmentKey ?? string.Empty,
            RepositoryKey = entity.PullRequestRepositoryKey ?? string.Empty,
            PullRequestId = entity.PullRequestId ?? string.Empty,
            PullRequestUrl = entity.PullRequestUrl,
            SourceBranch = entity.BranchName,
            TargetBranch = entity.PullRequestTargetBranch ?? string.Empty,
            SourceCommitSha = entity.BaseCommitSha,
        };

        return new ReworkCycle
        {
            Id = entity.Id,
            RequestMode = (ReworkRequestMode)entity.RequestMode,
            PullRequest = pullRequest,
            WorkItemId = entity.WorkItemId,
            CycleNumber = entity.CycleNumber,
            PriorRunId = entity.PriorRunId,
            BranchName = entity.BranchName,
            PullRequestUrl = entity.PullRequestUrl,
            BaseCommitSha = entity.BaseCommitSha,
            FeedbackBundleJson = entity.FeedbackBundleJson,
            FeedbackBundleId = entity.FeedbackBundleId,
            CorrelationId = entity.CorrelationId,
            Status = (ReworkCycleStatus)entity.Status,
            CreatedAt = entity.CreatedAt,
            ReactivatedAt = entity.ReactivatedAt,
            ConsumedAt = entity.ConsumedAt,
            NewRunId = entity.NewRunId,
        };
    }

    private static string FirstNonEmpty(string primary, string fallback) =>
        string.IsNullOrWhiteSpace(primary) ? fallback : primary;

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static string GenerateId(string prefix) => $"{prefix}_{Guid.NewGuid():N}";
}

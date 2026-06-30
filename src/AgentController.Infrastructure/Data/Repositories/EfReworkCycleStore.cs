using AgentController.Application;
using AgentController.Domain;
using AgentController.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace AgentController.Infrastructure.Data.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IReworkCycleStore"/> using SQLite.
/// Supports materializing Pending cycles from soaked feedback and
/// marking them Consumed at claim time.
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

    public async Task<ReworkCycle> CreateAsync(
        string workItemId,
        int cycleNumber,
        string priorRunId,
        string branchName,
        string pullRequestUrl,
        string baseCommitSha,
        string feedbackBundleJson,
        string feedbackBundleId,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var entity = new ReworkCycleEntity
        {
            Id = GenerateId("rcycle"),
            WorkItemId = workItemId,
            CycleNumber = cycleNumber,
            PriorRunId = priorRunId,
            BranchName = branchName,
            PullRequestUrl = pullRequestUrl,
            BaseCommitSha = baseCommitSha,
            FeedbackBundleJson = feedbackBundleJson,
            FeedbackBundleId = feedbackBundleId,
            Status = (int)ReworkCycleStatus.Pending,
            CreatedAt = now,
        };

        _db.ReworkCycles.Add(entity);
        await _db.SaveChangesAsync(cancellationToken);

        return MapToDomain(entity);
    }

    public async Task MarkConsumedAsync(
        string id,
        string newRunId,
        CancellationToken cancellationToken)
    {
        var entity = await _db.ReworkCycles
            .FindAsync([id], cancellationToken);

        if (entity is null)
            return;

        // Idempotent: no-op if already consumed.
        if (entity.Status == (int)ReworkCycleStatus.Consumed)
            return;

        entity.Status = (int)ReworkCycleStatus.Consumed;
        entity.ConsumedAt = DateTimeOffset.UtcNow;
        entity.NewRunId = newRunId;

        await _db.SaveChangesAsync(cancellationToken);
    }

    private static ReworkCycle MapToDomain(ReworkCycleEntity entity)
    {
        return new ReworkCycle
        {
            Id = entity.Id,
            WorkItemId = entity.WorkItemId,
            CycleNumber = entity.CycleNumber,
            PriorRunId = entity.PriorRunId,
            BranchName = entity.BranchName,
            PullRequestUrl = entity.PullRequestUrl,
            BaseCommitSha = entity.BaseCommitSha,
            FeedbackBundleJson = entity.FeedbackBundleJson,
            FeedbackBundleId = entity.FeedbackBundleId,
            Status = (ReworkCycleStatus)entity.Status,
            CreatedAt = entity.CreatedAt,
            ConsumedAt = entity.ConsumedAt,
            NewRunId = entity.NewRunId,
        };
    }

    private static string GenerateId(string prefix)
    {
        return $"{prefix}_{Guid.NewGuid():N}";
    }
}

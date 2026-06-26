using AgentController.Application;
using AgentController.Domain;
using AgentController.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace AgentController.Infrastructure.Data.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IAgentRunStore"/> using SQLite.
/// Supports run creation, status transitions, runtime field updates,
/// listing, and stale-run detection.
/// </summary>
internal sealed class EfAgentRunStore : IAgentRunStore
{
    private readonly AgentControllerDbContext _db;

    public EfAgentRunStore(AgentControllerDbContext db)
    {
        _db = db;
    }

    public async Task<AgentRunHandle> CreateAsync(
        CreateRunRequest request,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var entity = new AgentRunEntity
        {
            Id = GenerateId("run"),
            WorkItemId = request.WorkItemId,
            WorkerId = request.WorkerId,
            RuntimeType = request.RuntimeType,
            Status = (int)request.InitialStatus,
            RunAttempt = request.RunAttempt,
            PreviousRunId = request.PreviousRunId,
            CreatedAt = now,
            UpdatedAt = now,
        };

        _db.AgentRuns.Add(entity);
        await _db.SaveChangesAsync(cancellationToken);

        return MapToHandle(entity);
    }

    public async Task<AgentRunHandle?> GetByIdAsync(
        string runId,
        CancellationToken cancellationToken)
    {
        var entity = await _db.AgentRuns.FindAsync([runId], cancellationToken);
        return entity is null ? null : MapToHandle(entity);
    }

    public async Task UpdateStatusAsync(
        string runId,
        RunLifecycleState status,
        CancellationToken cancellationToken)
    {
        var entity = await _db.AgentRuns.FindAsync([runId], cancellationToken);
        if (entity is null)
            return;

        entity.Status = (int)status;
        entity.UpdatedAt = DateTimeOffset.UtcNow;

        // Set started time on first transition from claimed
        if (entity.StartedAt == null && status > RunLifecycleState.Claimed)
            entity.StartedAt = DateTimeOffset.UtcNow;

        // Set finished time on terminal states
        if (IsTerminalState(status) && entity.FinishedAt == null)
            entity.FinishedAt = DateTimeOffset.UtcNow;

        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateRuntimeFieldsAsync(
        string runId,
        RuntimeFieldUpdate update,
        CancellationToken cancellationToken)
    {
        var entity = await _db.AgentRuns.FindAsync([runId], cancellationToken);
        if (entity is null)
            return;

        var hasChange = false;

        if (update.RuntimeRunId is not null)
        {
            entity.RuntimeRunId = update.RuntimeRunId;
            hasChange = true;
        }
        if (update.RuntimeType is not null)
        {
            entity.RuntimeType = update.RuntimeType;
            hasChange = true;
        }
        if (update.EnvironmentId is not null)
        {
            entity.EnvironmentId = update.EnvironmentId;
            hasChange = true;
        }
        if (update.BranchName is not null)
        {
            entity.BranchName = update.BranchName;
            hasChange = true;
        }
        if (update.PullRequestUrl is not null)
        {
            entity.PullRequestUrl = update.PullRequestUrl;
            hasChange = true;
        }
        if (update.ResultSummary is not null)
        {
            entity.ResultSummary = update.ResultSummary;
            hasChange = true;
        }
        if (update.StartedAt is not null)
        {
            entity.StartedAt = update.StartedAt;
            hasChange = true;
        }
        if (update.FinishedAt is not null)
        {
            entity.FinishedAt = update.FinishedAt;
            hasChange = true;
        }
        if (update.LastHeartbeatAt is not null)
        {
            entity.LastHeartbeatAt = update.LastHeartbeatAt;
            hasChange = true;
        }
        if (update.Error is not null)
        {
            entity.Error = update.Error;
            hasChange = true;
        }
        if (update.RunAttempt is not null)
        {
            entity.RunAttempt = update.RunAttempt.Value;
            hasChange = true;
        }
        if (update.PreviousRunId is not null)
        {
            entity.PreviousRunId = update.PreviousRunId;
            hasChange = true;
        }

        if (hasChange)
        {
            entity.UpdatedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task<IReadOnlyList<AgentRunHandle>> ListAsync(
        ListRunsQuery query,
        CancellationToken cancellationToken)
    {
        IQueryable<AgentRunEntity> q = _db.AgentRuns;

        if (query.Status.HasValue)
            q = q.Where(e => e.Status == (int)query.Status.Value);

        if (!string.IsNullOrWhiteSpace(query.WorkItemId))
            q = q.Where(e => e.WorkItemId == query.WorkItemId);

        // Fetch entities then apply client-side ordering and pagination.
        // DateTimeOffset ORDER BY is not supported by EF Core SQLite 9.x.
        var entities = await q.ToListAsync(cancellationToken);

        IEnumerable<AgentRunEntity> ordered = entities.OrderByDescending(e => e.CreatedAt);

        if (query.Offset > 0)
            ordered = ordered.Skip(query.Offset);

        if (query.MaxResults > 0)
            ordered = ordered.Take(query.MaxResults);

        return ordered.Select(MapToHandle).ToList();
    }

    public async Task<IReadOnlyList<AgentRunHandle>> FindStaleAsync(
        TimeSpan staleTimeout,
        CancellationToken cancellationToken)
    {
        var cutoff = DateTimeOffset.UtcNow - staleTimeout;

        // Find runs in AwaitingResult whose last heartbeat (or started time) is older than cutoff.
        // Client evaluation is used because EF Core SQLite 9.x cannot translate
        // DateTimeOffset? comparisons or DateTimeOffset ORDER BY clauses.
        var entities = await _db.AgentRuns
            .Where(e => e.Status == (int)RunLifecycleState.AwaitingResult)
            .ToListAsync(cancellationToken);

        var stale = entities
            .Where(e =>
                (e.LastHeartbeatAt == null && e.StartedAt < cutoff) ||
                (e.LastHeartbeatAt < cutoff))
            .OrderBy(e => e.LastHeartbeatAt ?? e.StartedAt)
            .ToList();

        return stale.Select(MapToHandle).ToList();
    }

    public async Task<int> CountActiveAsync(CancellationToken cancellationToken)
    {
        return await _db.AgentRuns
            .CountAsync(
                e => e.Status != (int)RunLifecycleState.Completed
                     && e.Status != (int)RunLifecycleState.Failed
                     && e.Status != (int)RunLifecycleState.Cancelled
                     && e.Status != (int)RunLifecycleState.CleanedUp,
                cancellationToken);
    }

    private static AgentRunHandle MapToHandle(AgentRunEntity entity)
    {
        return new AgentRunHandle
        {
            RunId = entity.Id,
            WorkItemId = entity.WorkItemId,
            EnvironmentId = entity.EnvironmentId,
            RuntimeType = entity.RuntimeType,
            RuntimeRunId = entity.RuntimeRunId,
            Status = (RunLifecycleState)entity.Status,
            BranchName = entity.BranchName,
            PullRequestUrl = entity.PullRequestUrl,
            ResultSummary = entity.ResultSummary,
            StartedAt = entity.StartedAt,
            FinishedAt = entity.FinishedAt,
            LastHeartbeatAt = entity.LastHeartbeatAt,
            Error = entity.Error,
            RunAttempt = entity.RunAttempt,
            PreviousRunId = entity.PreviousRunId,
            CreatedAt = entity.CreatedAt,
            UpdatedAt = entity.UpdatedAt,
        };
    }

    private static bool IsTerminalState(RunLifecycleState status)
    {
        return status switch
        {
            RunLifecycleState.Completed => true,
            RunLifecycleState.Failed => true,
            RunLifecycleState.Cancelled => true,
            RunLifecycleState.CleanedUp => true,
            _ => false,
        };
    }

    public async Task<AgentRunHandle?> FindLatestRunByWorkItemAsync(
        string workItemId,
        CancellationToken cancellationToken)
    {
        var entity = await _db.AgentRuns
            .Where(e => e.WorkItemId == workItemId)
            .OrderByDescending(e => e.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        return entity is null ? null : MapToHandle(entity);
    }

    private static string GenerateId(string prefix)
    {
        return $"{prefix}_{Guid.NewGuid():N}";
    }
}

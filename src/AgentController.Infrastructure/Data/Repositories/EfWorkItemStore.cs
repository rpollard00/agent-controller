using System.Text.Json;
using AgentController.Application;
using AgentController.Domain;
using AgentController.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace AgentController.Infrastructure.Data.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IWorkItemStore"/> using SQLite.
/// Supports transactional local work claim/lease behavior and persisted
/// status updates through <see cref="AgentControllerDbContext"/>.
/// </summary>
internal sealed class EfWorkItemStore : IWorkItemStore
{
    private readonly AgentControllerDbContext _db;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public EfWorkItemStore(AgentControllerDbContext db)
    {
        _db = db;
    }

    public async Task<WorkCandidate> CreateAsync(
        CreateWorkItemRequest request,
        CancellationToken cancellationToken)
    {
        var entity = new WorkItemEntity
        {
            Id = GenerateId("wi"),
            ExternalSource = request.Source,
            ExternalId = "local-fake",
            RepoKey = request.RepoKey,
            Title = request.Title,
            Body = request.Body ?? request.Description,
            AcceptanceCriteriaJson = SerializeDictionary(request.AcceptanceCriteria),
            Priority = request.Priority,
            Status = request.Status,
            TagsJson = SerializeList(request.Tags),
            Source = request.Source,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        _db.WorkItems.Add(entity);
        await _db.SaveChangesAsync(cancellationToken);

        return MapToCandidate(entity);
    }

    public async Task<IReadOnlyList<WorkCandidate>> ListAsync(
        WorkItemListQuery query,
        CancellationToken cancellationToken)
    {
        IQueryable<WorkItemEntity> q = _db.WorkItems;

        if (!string.IsNullOrWhiteSpace(query.Status))
            q = q.Where(e => e.Status == query.Status);

        if (!string.IsNullOrWhiteSpace(query.RepoKey))
            q = q.Where(e => e.RepoKey == query.RepoKey);

        if (query.Tags is { Count: > 0 })
        {
            // Filter items whose TagsJson contains at least one of the requested tags.
            // SQLite JSON functions are used for this.
            foreach (var tag in query.Tags)
            {
                var t = tag;
                q = q.Where(e => EF.Functions.Like(e.TagsJson!, $"%{t}%"));
            }
        }

        q = q.OrderByDescending(e => e.CreatedAt);

        if (query.Offset > 0)
            q = q.Skip(query.Offset);

        if (query.MaxResults > 0)
            q = q.Take(query.MaxResults);

        var entities = await q.ToListAsync(cancellationToken);
        return entities.Select(MapToCandidate).ToList();
    }

    public async Task<WorkCandidate?> GetByIdAsync(
        string id,
        CancellationToken cancellationToken)
    {
        var entity = await _db.WorkItems.FindAsync([id], cancellationToken);
        return entity is null ? null : MapToCandidate(entity);
    }

    public async Task<IReadOnlyList<WorkCandidate>> FindEligibleAsync(
        WorkQuery query,
        CancellationToken cancellationToken)
    {
        IQueryable<WorkItemEntity> q = _db.WorkItems;

        // Filter by status if states are provided
        if (query.States is { Count: > 0 })
            q = q.Where(e => e.Status != null && query.States.Contains(e.Status));

        // Filter by eligible tags
        if (query.Tags is { Count: > 0 })
        {
            foreach (var tag in query.Tags)
            {
                var t = tag;
                q = q.Where(e => EF.Functions.Like(e.TagsJson!, $"%{t}%"));
            }
        }

        // Exclude items with excluded tags
        if (query.ExcludedTags is { Count: > 0 })
        {
            foreach (var excluded in query.ExcludedTags)
            {
                var ex = excluded;
                q = q.Where(e => !EF.Functions.Like(e.TagsJson!, $"%{ex}%"));
            }
        }

        // SQLite provider limitations: EF Core 9.x cannot translate
        // DateTimeOffset? comparisons or DateTimeOffset ORDER BY clauses
        // into SQL. We apply status/tag filters server-side and perform
        // lease filtering, priority range, ordering, and limit on the
        // client side. This is acceptable for local SQLite databases
        // with moderate item counts.

        var entities = await q.ToListAsync(cancellationToken);

        // Client-side filters
        var now = DateTimeOffset.UtcNow;
        IEnumerable<WorkItemEntity> filtered = entities;

        // Priority range filter
        if (query.PriorityMin.HasValue)
            filtered = filtered.Where(e => e.Priority != null && e.Priority >= query.PriorityMin.Value);

        if (query.PriorityMax.HasValue)
            filtered = filtered.Where(e => e.Priority != null && e.Priority <= query.PriorityMax.Value);

        // Lease filter: unclaimed or expired (client-side to avoid
        // DateTimeOffset? translation issues in EF Core SQLite 9.x)
        filtered = filtered.Where(e =>
            e.LeaseOwner == null || e.LeaseExpiresAt < now);

        // Client-side ordering (DateTimeOffset ORDER BY not supported in SQLite)
        filtered = filtered
            .OrderByDescending(e => e.Priority)
            .ThenBy(e => e.CreatedAt);

        if (query.MaxResults > 0)
            filtered = filtered.Take(query.MaxResults);

        return filtered.Select(MapToCandidate).ToList();
    }

    public async Task<ClaimResult> TryClaimAsync(
        string workItemId,
        ClaimRequest claim,
        CancellationToken cancellationToken)
    {
        // Use a transaction for atomic claim behavior.
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

        var entity = await _db.WorkItems.FindAsync([workItemId], cancellationToken);
        if (entity is null)
        {
            return new ClaimResult
            {
                Success = false,
                FailureReason = $"Work item '{workItemId}' not found.",
            };
        }

        var now = DateTimeOffset.UtcNow;

        // Check if the item is already claimed with an active lease
        if (entity.LeaseOwner != null && entity.LeaseExpiresAt >= now)
        {
            return new ClaimResult
            {
                Success = false,
                FailureReason = $"Work item '{workItemId}' is already claimed by '{entity.LeaseOwner}' (lease expires {entity.LeaseExpiresAt:O}).",
            };
        }

        // Claim the item
        entity.LeaseOwner = claim.WorkerId;
        entity.LeaseExpiresAt = now + claim.LeaseTimeout;
        entity.UpdatedAt = now;

        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new ClaimResult
        {
            Success = true,
            WorkRef = new ExternalWorkRef
            {
                Source = entity.Source,
                ExternalId = entity.ExternalId,
                Url = entity.ExternalUrl,
            },
            LeaseToken = entity.Id, // For SQLite, the work item ID serves as the lease token
        };
    }

    public async Task UpdateStatusAsync(
        string workItemId,
        string status,
        CancellationToken cancellationToken)
    {
        var entity = await _db.WorkItems.FindAsync([workItemId], cancellationToken);
        if (entity is null)
            return;

        entity.Status = status;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<WorkCandidate> UpsertAsync(
        WorkCandidate candidate,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        // Look up existing by (Source, ExternalId)
        var existing = await _db.WorkItems
            .FirstOrDefaultAsync(
                e => e.Source == candidate.Source && e.ExternalId == candidate.ExternalId,
                cancellationToken);

        if (existing is not null)
        {
            // Update mutable fields from the freshly discovered candidate.
            // Lease fields are intentionally not cleared — they are managed
            // exclusively by TryClaimAsync and should not be affected by
            // discovery upserts.
            existing.Title = candidate.Title;
            existing.Body = candidate.Description;
            existing.RepoKey = candidate.RepoKey;
            existing.Priority = candidate.Priority;
            existing.Status = candidate.Status;
            existing.AssignedTo = candidate.AssignedTo;
            existing.ExternalUrl = candidate.ExternalUrl;
            existing.AcceptanceCriteriaJson = SerializeDictionary(candidate.AcceptanceCriteria);
            existing.TagsJson = SerializeList(candidate.Tags);
            existing.SourceMetadataJson = SerializeDictionary(candidate.SourceMetadata);
            existing.UpdatedAt = now;

            await _db.SaveChangesAsync(cancellationToken);
            return MapToCandidate(existing);
        }

        // Insert new record with a controller-assigned ID.
        // The ID uses the candidate's ExternalId as a stable suffix so
        // it can be reconstructed from source data.
        var entity = new WorkItemEntity
        {
            Id = GenerateId($"wi_{candidate.ExternalId}"),
            ExternalSource = candidate.Source,
            ExternalId = candidate.ExternalId,
            ExternalUrl = candidate.ExternalUrl,
            RepoKey = candidate.RepoKey,
            Title = candidate.Title,
            Body = candidate.Description,
            AcceptanceCriteriaJson = SerializeDictionary(candidate.AcceptanceCriteria),
            Priority = candidate.Priority,
            Status = candidate.Status,
            TagsJson = SerializeList(candidate.Tags),
            AssignedTo = candidate.AssignedTo,
            Source = candidate.Source,
            SourceMetadataJson = SerializeDictionary(candidate.SourceMetadata),
            CreatedAt = now,
            UpdatedAt = now,
        };

        _db.WorkItems.Add(entity);
        await _db.SaveChangesAsync(cancellationToken);

        return MapToCandidate(entity);
    }

    private static WorkCandidate MapToCandidate(WorkItemEntity entity)
    {
        return new WorkCandidate
        {
            Id = entity.Id,
            ExternalId = entity.ExternalId,
            ExternalUrl = entity.ExternalUrl,
            RepoKey = entity.RepoKey,
            Title = entity.Title,
            Description = entity.Body,
            AcceptanceCriteria = DeserializeDictionary(entity.AcceptanceCriteriaJson),
            Priority = entity.Priority,
            Status = entity.Status,
            Tags = DeserializeList(entity.TagsJson),
            AssignedTo = entity.AssignedTo,
            Source = entity.Source,
            SourceMetadata = DeserializeDictionary(entity.SourceMetadataJson),
        };
    }

    private static string? SerializeDictionary(IReadOnlyDictionary<string, string>? dict)
    {
        return dict is { Count: > 0 } ? JsonSerializer.Serialize(dict, JsonOptions) : null;
    }

    private static string? SerializeList(IReadOnlyList<string>? list)
    {
        return list is { Count: > 0 } ? JsonSerializer.Serialize(list, JsonOptions) : null;
    }

    private static Dictionary<string, string>? DeserializeDictionary(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static List<string> DeserializeList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json, JsonOptions) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static string GenerateId(string prefix)
    {
        return $"{prefix}_{Guid.NewGuid():N}";
    }
}

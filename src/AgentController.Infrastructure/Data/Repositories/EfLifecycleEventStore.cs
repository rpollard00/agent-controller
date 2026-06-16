using System.Text.Json;
using AgentController.Application;
using AgentController.Domain;
using AgentController.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace AgentController.Infrastructure.Data.Repositories;

/// <summary>
/// EF Core implementation of <see cref="ILifecycleEventStore"/> using SQLite.
/// Supports append-only event recording, listing by run, and idempotency
/// checking by external event ID.
/// </summary>
internal sealed class EfLifecycleEventStore : ILifecycleEventStore
{
    private readonly AgentControllerDbContext _db;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public EfLifecycleEventStore(AgentControllerDbContext db)
    {
        _db = db;
    }

    public async Task AppendAsync(
        LifecycleEvent evt,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var entity = new LifecycleEventEntity
        {
            Id = string.IsNullOrWhiteSpace(evt.Id)
                ? GenerateId("evt")
                : evt.Id,
            RunId = evt.RunId,
            EventId = evt.EventId,
            EventType = evt.EventType,
            Severity = (int)evt.Severity,
            Message = evt.Message,
            PayloadJson = SerializePayload(evt.Payload),
            CreatedAt = evt.CreatedAt == default ? now : evt.CreatedAt,
            UpdatedAt = now,
        };

        _db.LifecycleEvents.Add(entity);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<LifecycleEvent>> ListByRunIdAsync(
        string runId,
        CancellationToken cancellationToken)
    {
        // Fetch entities then apply client-side ordering.
        // DateTimeOffset ORDER BY is not supported by EF Core SQLite 9.x.
        var entities = await _db.LifecycleEvents
            .Where(e => e.RunId == runId)
            .ToListAsync(cancellationToken);

        return entities.OrderBy(e => e.CreatedAt).Select(MapToDomain).ToList();
    }

    public async Task<bool> ExistsByEventIdAsync(
        string runId,
        string eventId,
        CancellationToken cancellationToken)
    {
        return await _db.LifecycleEvents
            .AnyAsync(e => e.RunId == runId && e.EventId == eventId, cancellationToken);
    }

    private static LifecycleEvent MapToDomain(LifecycleEventEntity entity)
    {
        return new LifecycleEvent
        {
            Id = entity.Id,
            RunId = entity.RunId,
            EventId = entity.EventId,
            EventType = entity.EventType,
            Severity = (EventSeverity)entity.Severity,
            Message = entity.Message,
            Payload = DeserializePayload(entity.PayloadJson),
            CreatedAt = entity.CreatedAt,
        };
    }

    private static string? SerializePayload(IReadOnlyDictionary<string, object?>? payload)
    {
        if (payload is not { Count: > 0 }) return null;
        try
        {
            return JsonSerializer.Serialize(payload, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static Dictionary<string, object?>? DeserializePayload(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, object?>>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static string GenerateId(string prefix)
    {
        return $"{prefix}_{Guid.NewGuid():N}";
    }
}

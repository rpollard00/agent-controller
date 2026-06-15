using System.Text.Json;
using AgentController.Application;
using AgentController.Domain;
using AgentController.Infrastructure.Data.Entities;

namespace AgentController.Infrastructure.Data.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IEnvironmentStore"/> using SQLite.
/// Supports environment record creation and status updates.
/// </summary>
internal sealed class EfEnvironmentStore : IEnvironmentStore
{
    private readonly AgentControllerDbContext _db;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public EfEnvironmentStore(AgentControllerDbContext db)
    {
        _db = db;
    }

    public async Task<EnvironmentHandle> CreateAsync(
        CreateEnvironmentRequest request,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var entity = new EnvironmentEntity
        {
            Id = GenerateId("env"),
            ProviderType = request.ProviderType,
            RunId = request.RunId,
            RootPath = request.RootPath,
            Status = request.Status,
            MetadataJson = SerializeMetadata(request.Metadata),
            CreatedAt = now,
            UpdatedAt = now,
        };

        _db.Environments.Add(entity);
        await _db.SaveChangesAsync(cancellationToken);

        return new EnvironmentHandle
        {
            Id = entity.Id,
            ProviderType = entity.ProviderType,
            RootPath = entity.RootPath,
            Status = entity.Status,
        };
    }

    public async Task<EnvironmentHandle?> GetByIdAsync(
        string environmentId,
        CancellationToken cancellationToken)
    {
        var entity = await _db.Environments.FindAsync([environmentId], cancellationToken);
        return entity is null ? null : new EnvironmentHandle
        {
            Id = entity.Id,
            ProviderType = entity.ProviderType,
            RootPath = entity.RootPath,
            Status = entity.Status,
        };
    }

    public async Task UpdateStatusAsync(
        string environmentId,
        string status,
        CancellationToken cancellationToken)
    {
        var entity = await _db.Environments.FindAsync([environmentId], cancellationToken);
        if (entity is null)
            return;

        entity.Status = status;
        entity.UpdatedAt = DateTimeOffset.UtcNow;

        if (status.Equals("destroyed", StringComparison.OrdinalIgnoreCase) && entity.DestroyedAt == null)
            entity.DestroyedAt = DateTimeOffset.UtcNow;

        await _db.SaveChangesAsync(cancellationToken);
    }

    private static string? SerializeMetadata(IReadOnlyDictionary<string, string>? metadata)
    {
        if (metadata is not { Count: > 0 }) return null;
        try
        {
            return JsonSerializer.Serialize(metadata, JsonOptions);
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

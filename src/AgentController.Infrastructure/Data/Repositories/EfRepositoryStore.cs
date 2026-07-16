using System.Text.Json;
using AgentController.Application;
using AgentController.Domain;
using AgentController.Infrastructure.Data.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AgentController.Infrastructure.Data.Repositories;

/// <summary>
/// SQLite-backed persistence for managed repository profiles.
/// </summary>
internal sealed class EfRepositoryStore : IRepositoryStore
{
    private readonly AgentControllerDbContext _db;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public EfRepositoryStore(AgentControllerDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<RepositoryProfile>> ListAsync(
        CancellationToken cancellationToken
    )
    {
        var entities = await _db
            .Repositories.AsNoTracking()
            .OrderBy(x => x.Key)
            .ToListAsync(cancellationToken);

        return entities.Select(MapToProfile).ToList();
    }

    public async Task<RepositoryProfile?> GetByKeyAsync(
        string key,
        CancellationToken cancellationToken
    )
    {
        var entity = await _db
            .Repositories.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Key == key, cancellationToken);

        return entity is null ? null : MapToProfile(entity);
    }

    public async Task<bool> CreateAsync(
        RepositoryProfile profile,
        CancellationToken cancellationToken
    )
    {
        if (await _db.Repositories.AnyAsync(x => x.Key == profile.Key, cancellationToken))
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        var entity = MapToEntity(profile, now);
        _db.Repositories.Add(entity);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException exception) when (IsUniqueConstraintViolation(exception))
        {
            _db.Entry(entity).State = EntityState.Detached;
            return false;
        }
    }

    public async Task<bool> UpdateAsync(
        RepositoryProfile profile,
        CancellationToken cancellationToken
    )
    {
        var entity = await _db.Repositories.FindAsync([profile.Key], cancellationToken);

        if (entity is null)
        {
            return false;
        }

        ApplyProfile(entity, profile);
        entity.UpdatedAt = DateTimeOffset.UtcNow;

        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> DeleteAsync(string key, CancellationToken cancellationToken)
    {
        var entity = await _db.Repositories.FindAsync([key], cancellationToken);

        if (entity is null)
        {
            return false;
        }

        _db.Repositories.Remove(entity);
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task UpsertAsync(RepositoryProfile profile, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var entity = await _db.Repositories.FindAsync([profile.Key], cancellationToken);

        if (entity is null)
        {
            _db.Repositories.Add(MapToEntity(profile, now));
        }
        else
        {
            ApplyProfile(entity, profile);
            entity.UpdatedAt = now;
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    private static RepositoryEntity MapToEntity(RepositoryProfile profile, DateTimeOffset timestamp)
    {
        var entity = new RepositoryEntity
        {
            Key = profile.Key,
            CreatedAt = timestamp,
            UpdatedAt = timestamp,
        };
        ApplyProfile(entity, profile);
        return entity;
    }

    private static void ApplyProfile(RepositoryEntity entity, RepositoryProfile profile)
    {
        entity.CloneUrl = profile.CloneUrl;
        entity.DefaultBranch = profile.DefaultBranch;
        entity.Transport = profile.Transport;
        entity.EnvironmentProfile = profile.EnvironmentProfile;
        entity.RuntimeProfile = profile.RuntimeProfile;
        entity.RepositoryHostConnectionKey = profile.RepositoryHostConnectionKey;
        entity.RemoteIdentity = profile.RemoteIdentity;
        entity.RuntimeEnvironmentKey = profile.RuntimeEnvironmentKey;
        entity.AllowedPathsJson = SerializeList(profile.AllowedPaths);
    }

    private static RepositoryProfile MapToProfile(RepositoryEntity entity)
    {
        return new RepositoryProfile
        {
            Key = entity.Key,
            CloneUrl = entity.CloneUrl,
            DefaultBranch = entity.DefaultBranch,
            Transport = entity.Transport,
            EnvironmentProfile = entity.EnvironmentProfile,
            RuntimeProfile = entity.RuntimeProfile,
            RepositoryHostConnectionKey = entity.RepositoryHostConnectionKey,
            RemoteIdentity = entity.RemoteIdentity,
            RuntimeEnvironmentKey = entity.RuntimeEnvironmentKey,
            AllowedPaths = DeserializeList(entity.AllowedPathsJson),
        };
    }

    private static string? SerializeList(IReadOnlyList<string>? list)
    {
        if (list is not { Count: > 0 })
        {
            return null;
        }

        try
        {
            return JsonSerializer.Serialize(list, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static List<string> DeserializeList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json, JsonOptions) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static bool IsUniqueConstraintViolation(DbUpdateException exception)
    {
        return exception.InnerException
            is SqliteException { SqliteErrorCode: 19, SqliteExtendedErrorCode: 1555 or 2067 };
    }
}

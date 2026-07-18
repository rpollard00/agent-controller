using System.Text.Json;
using AgentController.Application;
using AgentController.Domain;
using AgentController.Domain.Secrets;
using AgentController.Infrastructure.Data.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AgentController.Infrastructure.Data.Repositories;

/// <summary>
/// SQLite-backed persistence for unified connection profiles.
/// </summary>
internal sealed class EfConnectionStore : IConnectionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly AgentControllerDbContext _db;

    public EfConnectionStore(AgentControllerDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<ConnectionProfile>> ListAsync(
        CancellationToken cancellationToken)
    {
        var entities = await _db.Connections
            .AsNoTracking()
            .OrderBy(x => x.Key)
            .ToListAsync(cancellationToken);

        return entities.Select(MapToProfile).ToList();
    }

    public async Task<ConnectionProfile?> GetByKeyAsync(
        string key,
        CancellationToken cancellationToken)
    {
        var entity = await _db.Connections
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Key == key, cancellationToken);

        return entity is null ? null : MapToProfile(entity);
    }

    public async Task<bool> CreateAsync(
        ConnectionProfile profile,
        CancellationToken cancellationToken)
    {
        if (await _db.Connections.AnyAsync(
                x => x.Key == profile.Key,
                cancellationToken))
        {
            return false;
        }

        var entity = MapToEntity(profile);
        _db.Connections.Add(entity);

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
        ConnectionProfile profile,
        CancellationToken cancellationToken)
    {
        var entity = await _db.Connections.FindAsync(
            [profile.Key],
            cancellationToken);

        if (entity is null)
        {
            return false;
        }

        entity.DisplayName = profile.DisplayName;
        entity.Enabled = profile.Enabled;
        entity.Provider = profile.Provider;
        entity.Capabilities = CapabilitiesToBitmask(profile.Capabilities);
        entity.ProviderSettingsJson = SerializeProviderSettings(profile.ProviderSettings);
        entity.UpdatedAt = profile.UpdatedAt;

        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> DeleteAsync(
        string key,
        CancellationToken cancellationToken)
    {
        var entity = await _db.Connections.FindAsync(
            [key],
            cancellationToken);

        if (entity is null)
        {
            return false;
        }

        _db.Connections.Remove(entity);
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static ConnectionEntity MapToEntity(ConnectionProfile profile)
    {
        return new ConnectionEntity
        {
            Key = profile.Key,
            DisplayName = profile.DisplayName,
            Enabled = profile.Enabled,
            Provider = profile.Provider,
            Capabilities = CapabilitiesToBitmask(profile.Capabilities),
            ProviderSettingsJson = SerializeProviderSettings(profile.ProviderSettings),
            CreatedAt = profile.CreatedAt,
            UpdatedAt = profile.UpdatedAt,
        };
    }

    private static ConnectionProfile MapToProfile(ConnectionEntity entity)
    {
        return new ConnectionProfile
        {
            Key = entity.Key,
            DisplayName = entity.DisplayName,
            Enabled = entity.Enabled,
            Provider = entity.Provider,
            Capabilities = BitmaskToCapabilities(entity.Capabilities),
            ProviderSettings = DeserializeProviderSettings(entity.Provider, entity.ProviderSettingsJson),
            CreatedAt = entity.CreatedAt,
            UpdatedAt = entity.UpdatedAt,
        };
    }

    private static int CapabilitiesToBitmask(IReadOnlyList<ConnectionCapability> capabilities)
    {
        int mask = 0;
        foreach (var cap in capabilities)
        {
            mask |= (int)cap;
        }
        return mask;
    }

    private static List<ConnectionCapability> BitmaskToCapabilities(int bitmask)
    {
        var list = new List<ConnectionCapability>();
        if ((bitmask & (int)ConnectionCapability.Repositories) != 0)
            list.Add(ConnectionCapability.Repositories);
        if ((bitmask & (int)ConnectionCapability.WorkTracking) != 0)
            list.Add(ConnectionCapability.WorkTracking);
        if ((bitmask & (int)ConnectionCapability.ExecutionHost) != 0)
            list.Add(ConnectionCapability.ExecutionHost);
        return list;
    }

    private static string? SerializeProviderSettings(ConnectionSettings? settings)
    {
        if (settings is null)
        {
            return null;
        }

        // Serialize using the concrete runtime type so derived properties are included.
        return JsonSerializer.Serialize(settings, settings.GetType(), JsonOptions);
    }

    // CA1859: return type is the abstract base because provider-specific subtypes
    // are selected at runtime; callers need the base type for the Profile property.
#pragma warning disable CA1859
    private static ConnectionSettings? DeserializeProviderSettings(
        string provider,
        string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        return provider switch
        {
            "AzureDevOps" => JsonSerializer.Deserialize<AzureDevOpsConnectionSettings>(json, JsonOptions),
            _ => null,
        };
    }
#pragma warning restore CA1859

    private static bool IsUniqueConstraintViolation(DbUpdateException exception)
    {
        return exception.InnerException is SqliteException
        {
            SqliteErrorCode: 19,
            SqliteExtendedErrorCode: 1555 or 2067,
        };
    }
}

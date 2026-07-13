using System.Text.Json;
using AgentController.Application;
using AgentController.Domain;
using AgentController.Infrastructure.Data.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AgentController.Infrastructure.Data.Repositories;

/// <summary>SQLite-backed persistence for managed runtime environment profiles.</summary>
internal sealed class EfRuntimeEnvironmentStore : IRuntimeEnvironmentStore
{
    private readonly AgentControllerDbContext _db;

    public EfRuntimeEnvironmentStore(AgentControllerDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<RuntimeEnvironmentProfile>> ListAsync(
        CancellationToken cancellationToken
    )
    {
        var entities = await _db
            .RuntimeEnvironments.AsNoTracking()
            .OrderBy(x => x.Key)
            .ToListAsync(cancellationToken);

        return entities.Select(MapToProfile).ToList();
    }

    public async Task<RuntimeEnvironmentProfile?> GetByKeyAsync(
        string key,
        CancellationToken cancellationToken
    )
    {
        var entity = await _db
            .RuntimeEnvironments.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Key == key, cancellationToken);

        return entity is null ? null : MapToProfile(entity);
    }

    public async Task<bool> CreateAsync(
        RuntimeEnvironmentProfile profile,
        CancellationToken cancellationToken
    )
    {
        if (await _db.RuntimeEnvironments.AnyAsync(x => x.Key == profile.Key, cancellationToken))
        {
            return false;
        }

        var entity = MapToEntity(profile);
        _db.RuntimeEnvironments.Add(entity);

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
        RuntimeEnvironmentProfile profile,
        CancellationToken cancellationToken
    )
    {
        var entity = await _db.RuntimeEnvironments.FindAsync([profile.Key], cancellationToken);

        if (entity is null)
        {
            return false;
        }

        entity.DisplayName = profile.DisplayName;
        entity.Enabled = profile.Enabled;
        entity.EnvironmentProvider = profile.EnvironmentProvider;
        entity.WorkspaceRoot = profile.EnvironmentSettings.WorkspaceRoot;
        entity.RuntimeProvider = profile.RuntimeProvider;
        entity.PiExecutablePath = profile.RuntimeSettings.PiExecutablePath;
        entity.ControllerBaseUrl = profile.RuntimeSettings.ControllerBaseUrl;
        entity.PtyWrapperPath = profile.RuntimeSettings.PtyWrapperPath;
        entity.PtyWrapperArgs = profile.RuntimeSettings.PtyWrapperArgs;
        entity.LoadoutsJson = SerializeLoadouts(profile.RuntimeSettings.Loadouts);
        entity.ForwardEnvironmentVariablesJson = SerializeEnvironmentVariableMappings(
            profile.RuntimeSettings.ForwardEnvironmentVariables
        );
        entity.UpdatedAt = profile.UpdatedAt;

        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> DeleteAsync(string key, CancellationToken cancellationToken)
    {
        var entity = await _db.RuntimeEnvironments.FindAsync([key], cancellationToken);

        if (entity is null)
        {
            return false;
        }

        _db.RuntimeEnvironments.Remove(entity);
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static RuntimeEnvironmentEntity MapToEntity(RuntimeEnvironmentProfile profile)
    {
        return new RuntimeEnvironmentEntity
        {
            Key = profile.Key,
            DisplayName = profile.DisplayName,
            Enabled = profile.Enabled,
            EnvironmentProvider = profile.EnvironmentProvider,
            WorkspaceRoot = profile.EnvironmentSettings.WorkspaceRoot,
            RuntimeProvider = profile.RuntimeProvider,
            PiExecutablePath = profile.RuntimeSettings.PiExecutablePath,
            ControllerBaseUrl = profile.RuntimeSettings.ControllerBaseUrl,
            PtyWrapperPath = profile.RuntimeSettings.PtyWrapperPath,
            PtyWrapperArgs = profile.RuntimeSettings.PtyWrapperArgs,
            LoadoutsJson = SerializeLoadouts(profile.RuntimeSettings.Loadouts),
            ForwardEnvironmentVariablesJson = SerializeEnvironmentVariableMappings(
                profile.RuntimeSettings.ForwardEnvironmentVariables
            ),
            CreatedAt = profile.CreatedAt,
            UpdatedAt = profile.UpdatedAt,
        };
    }

    private static RuntimeEnvironmentProfile MapToProfile(RuntimeEnvironmentEntity entity)
    {
        return new RuntimeEnvironmentProfile
        {
            Key = entity.Key,
            DisplayName = entity.DisplayName,
            Enabled = entity.Enabled,
            EnvironmentProvider = entity.EnvironmentProvider,
            EnvironmentSettings = new EnvironmentProviderSettings
            {
                WorkspaceRoot = entity.WorkspaceRoot,
            },
            RuntimeProvider = entity.RuntimeProvider,
            RuntimeSettings = new RuntimeProviderSettings
            {
                PiExecutablePath = entity.PiExecutablePath,
                ControllerBaseUrl = entity.ControllerBaseUrl,
                PtyWrapperPath = entity.PtyWrapperPath,
                PtyWrapperArgs = entity.PtyWrapperArgs,
                Loadouts = DeserializeLoadouts(entity.LoadoutsJson),
                ForwardEnvironmentVariables = DeserializeEnvironmentVariableMappings(
                    entity.ForwardEnvironmentVariablesJson
                ),
            },
            CreatedAt = entity.CreatedAt,
            UpdatedAt = entity.UpdatedAt,
        };
    }

    private static string SerializeLoadouts(IReadOnlyDictionary<ExecutionKind, string> loadouts)
    {
        var ordered = loadouts.OrderBy(x => x.Key).ToDictionary(x => x.Key, x => x.Value);
        return JsonSerializer.Serialize(ordered);
    }

    private static Dictionary<ExecutionKind, string> DeserializeLoadouts(string json)
    {
        return JsonSerializer.Deserialize<Dictionary<ExecutionKind, string>>(json) ?? [];
    }

    private static string SerializeEnvironmentVariableMappings(
        IReadOnlyDictionary<string, string> mappings
    )
    {
        var ordered = mappings
            .OrderBy(x => x.Key, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
        return JsonSerializer.Serialize(ordered);
    }

    private static Dictionary<string, string> DeserializeEnvironmentVariableMappings(string json)
    {
        return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [];
    }

    private static bool IsUniqueConstraintViolation(DbUpdateException exception)
    {
        return exception.InnerException
            is SqliteException { SqliteErrorCode: 19, SqliteExtendedErrorCode: 1555 or 2067 };
    }
}

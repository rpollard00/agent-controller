using System.Text.Json;
using AgentController.Application;
using AgentController.Domain;
using AgentController.Infrastructure.Data.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AgentController.Infrastructure.Data.Repositories;

/// <summary>
/// SQLite-backed persistence for managed Azure DevOps environment profiles.
/// </summary>
internal sealed class EfAzureDevOpsEnvironmentStore : IAzureDevOpsEnvironmentStore
{
    private readonly AgentControllerDbContext _db;

    public EfAzureDevOpsEnvironmentStore(AgentControllerDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<AzureDevOpsEnvironmentProfile>> ListAsync(
        CancellationToken cancellationToken)
    {
        var entities = await _db.AzureDevOpsEnvironments
            .AsNoTracking()
            .OrderBy(x => x.Key)
            .ToListAsync(cancellationToken);

        return entities.Select(MapToProfile).ToList();
    }

    public async Task<AzureDevOpsEnvironmentProfile?> GetByKeyAsync(
        string key,
        CancellationToken cancellationToken)
    {
        var entity = await _db.AzureDevOpsEnvironments
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Key == key, cancellationToken);

        return entity is null ? null : MapToProfile(entity);
    }

    public async Task<bool> CreateAsync(
        AzureDevOpsEnvironmentProfile profile,
        CancellationToken cancellationToken)
    {
        if (await _db.AzureDevOpsEnvironments.AnyAsync(
                x => x.Key == profile.Key,
                cancellationToken))
        {
            return false;
        }

        var entity = MapToEntity(profile);
        _db.AzureDevOpsEnvironments.Add(entity);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException exception) when (IsUniqueConstraintViolation(exception))
        {
            // A concurrent creator can win after the existence check. Preserve the
            // store contract and leave this scoped context usable by detaching the row.
            _db.Entry(entity).State = EntityState.Detached;
            return false;
        }
    }

    public async Task<bool> UpdateAsync(
        AzureDevOpsEnvironmentProfile profile,
        CancellationToken cancellationToken)
    {
        var entity = await _db.AzureDevOpsEnvironments.FindAsync(
            [profile.Key],
            cancellationToken);

        if (entity is null)
        {
            return false;
        }

        entity.DisplayName = profile.DisplayName;
        entity.Enabled = profile.Enabled;
        entity.OrganizationUrl = profile.OrganizationUrl;
        entity.Project = profile.Project;
        entity.WorkItemType = profile.WorkItemType;
        entity.EligibleTagsJson = SerializeList(profile.EligibleTags);
        entity.ExcludedTagsJson = SerializeList(profile.ExcludedTags);
        entity.EligibleStatesJson = SerializeList(profile.EligibleStates);
        entity.ExcludedStatesJson = SerializeList(profile.ExcludedStates);
        entity.ActiveState = profile.ActiveState;
        entity.CompletedState = profile.CompletedState;
        entity.PatEnvironmentVariable = profile.PatEnvironmentVariable;
        entity.UpdatedAt = profile.UpdatedAt;

        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> DeleteAsync(
        string key,
        CancellationToken cancellationToken)
    {
        var entity = await _db.AzureDevOpsEnvironments.FindAsync(
            [key],
            cancellationToken);

        if (entity is null)
        {
            return false;
        }

        _db.AzureDevOpsEnvironments.Remove(entity);
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static AzureDevOpsEnvironmentEntity MapToEntity(
        AzureDevOpsEnvironmentProfile profile)
    {
        return new AzureDevOpsEnvironmentEntity
        {
            Key = profile.Key,
            DisplayName = profile.DisplayName,
            Enabled = profile.Enabled,
            OrganizationUrl = profile.OrganizationUrl,
            Project = profile.Project,
            WorkItemType = profile.WorkItemType,
            EligibleTagsJson = SerializeList(profile.EligibleTags),
            ExcludedTagsJson = SerializeList(profile.ExcludedTags),
            EligibleStatesJson = SerializeList(profile.EligibleStates),
            ExcludedStatesJson = SerializeList(profile.ExcludedStates),
            ActiveState = profile.ActiveState,
            CompletedState = profile.CompletedState,
            PatEnvironmentVariable = profile.PatEnvironmentVariable,
            CreatedAt = profile.CreatedAt,
            UpdatedAt = profile.UpdatedAt,
        };
    }

    private static AzureDevOpsEnvironmentProfile MapToProfile(
        AzureDevOpsEnvironmentEntity entity)
    {
        return new AzureDevOpsEnvironmentProfile
        {
            Key = entity.Key,
            DisplayName = entity.DisplayName,
            Enabled = entity.Enabled,
            OrganizationUrl = entity.OrganizationUrl,
            Project = entity.Project,
            WorkItemType = entity.WorkItemType,
            EligibleTags = DeserializeList(entity.EligibleTagsJson),
            ExcludedTags = DeserializeList(entity.ExcludedTagsJson),
            EligibleStates = DeserializeList(entity.EligibleStatesJson),
            ExcludedStates = DeserializeList(entity.ExcludedStatesJson),
            ActiveState = entity.ActiveState,
            CompletedState = entity.CompletedState,
            PatEnvironmentVariable = entity.PatEnvironmentVariable,
            CreatedAt = entity.CreatedAt,
            UpdatedAt = entity.UpdatedAt,
        };
    }

    private static string SerializeList(IReadOnlyList<string> values)
    {
        return JsonSerializer.Serialize(values);
    }

    private static List<string> DeserializeList(string json)
    {
        return JsonSerializer.Deserialize<List<string>>(json) ?? [];
    }

    private static bool IsUniqueConstraintViolation(DbUpdateException exception)
    {
        return exception.InnerException is SqliteException
        {
            SqliteErrorCode: 19,
            SqliteExtendedErrorCode: 1555 or 2067,
        };
    }
}

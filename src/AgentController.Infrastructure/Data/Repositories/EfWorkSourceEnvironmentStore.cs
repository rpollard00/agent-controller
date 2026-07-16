using AgentController.Application;
using AgentController.Domain;
using AgentController.Infrastructure.Data.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AgentController.Infrastructure.Data.Repositories;

/// <summary>
/// SQLite-backed persistence for managed work source environment profiles.
/// </summary>
internal sealed class EfWorkSourceEnvironmentStore : IWorkSourceEnvironmentStore
{
    private readonly AgentControllerDbContext _db;

    public EfWorkSourceEnvironmentStore(AgentControllerDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<WorkSourceEnvironmentProfile>> ListAsync(
        CancellationToken cancellationToken)
    {
        var entities = await _db.WorkSourceEnvironments
            .AsNoTracking()
            .OrderBy(x => x.Key)
            .ToListAsync(cancellationToken);

        return entities.Select(MapToProfile).ToList();
    }

    public async Task<WorkSourceEnvironmentProfile?> GetByKeyAsync(
        string key,
        CancellationToken cancellationToken)
    {
        var entity = await _db.WorkSourceEnvironments
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Key == key, cancellationToken);

        return entity is null ? null : MapToProfile(entity);
    }

    public async Task<bool> CreateAsync(
        WorkSourceEnvironmentProfile profile,
        CancellationToken cancellationToken)
    {
        if (await _db.WorkSourceEnvironments.AnyAsync(
                x => x.Key == profile.Key,
                cancellationToken))
        {
            return false;
        }

        var entity = MapToEntity(profile);
        _db.WorkSourceEnvironments.Add(entity);

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
        WorkSourceEnvironmentProfile profile,
        CancellationToken cancellationToken)
    {
        var entity = await _db.WorkSourceEnvironments.FindAsync(
            [profile.Key],
            cancellationToken);

        if (entity is null)
        {
            return false;
        }

        entity.DisplayName = profile.DisplayName;
        entity.Enabled = profile.Enabled;
        entity.Provider = profile.Provider;
        entity.TagPrefix = profile.TagPrefix;
        entity.OrganizationUrl = profile.OrganizationUrl;
        entity.Project = profile.Project;
        entity.ActiveState = profile.ActiveState;
        entity.CompletedState = profile.CompletedState;
        entity.PersonalAccessTokenSecretName = profile.PersonalAccessTokenReference.Name;
        entity.PersonalAccessTokenSecretVersion = profile.PersonalAccessTokenReference.Version;
        entity.UpdatedAt = profile.UpdatedAt;

        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> DeleteAsync(
        string key,
        CancellationToken cancellationToken)
    {
        var entity = await _db.WorkSourceEnvironments.FindAsync(
            [key],
            cancellationToken);

        if (entity is null)
        {
            return false;
        }

        _db.WorkSourceEnvironments.Remove(entity);
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static WorkSourceEnvironmentEntity MapToEntity(
        WorkSourceEnvironmentProfile profile)
    {
        return new WorkSourceEnvironmentEntity
        {
            Key = profile.Key,
            DisplayName = profile.DisplayName,
            Enabled = profile.Enabled,
            Provider = profile.Provider,
            TagPrefix = profile.TagPrefix,
            OrganizationUrl = profile.OrganizationUrl,
            Project = profile.Project,
            ActiveState = profile.ActiveState,
            CompletedState = profile.CompletedState,
            PersonalAccessTokenSecretName = profile.PersonalAccessTokenReference.Name,
            PersonalAccessTokenSecretVersion = profile.PersonalAccessTokenReference.Version,
            CreatedAt = profile.CreatedAt,
            UpdatedAt = profile.UpdatedAt,
        };
    }

    private static WorkSourceEnvironmentProfile MapToProfile(
        WorkSourceEnvironmentEntity entity)
    {
        return new WorkSourceEnvironmentProfile
        {
            Key = entity.Key,
            DisplayName = entity.DisplayName,
            Enabled = entity.Enabled,
            Provider = entity.Provider,
            TagPrefix = entity.TagPrefix,
            OrganizationUrl = entity.OrganizationUrl,
            Project = entity.Project,
            ActiveState = entity.ActiveState,
            CompletedState = entity.CompletedState,
            PersonalAccessTokenReference = string.IsNullOrWhiteSpace(entity.PersonalAccessTokenSecretName)
                ? AgentController.Domain.Secrets.SecretReference.Empty
                : new AgentController.Domain.Secrets.SecretReference
                {
                    Name = entity.PersonalAccessTokenSecretName,
                    Version = entity.PersonalAccessTokenSecretVersion,
                },
            CreatedAt = entity.CreatedAt,
            UpdatedAt = entity.UpdatedAt,
        };
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

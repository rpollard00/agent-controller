using AgentController.Application;
using AgentController.Domain;
using AgentController.Infrastructure.Data.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AgentController.Infrastructure.Data.Repositories;

/// <summary>
/// SQLite-backed persistence for managed repository host connection profiles.
/// </summary>
internal sealed class EfRepositoryHostConnectionStore : IRepositoryHostConnectionStore
{
    private readonly AgentControllerDbContext _db;

    public EfRepositoryHostConnectionStore(AgentControllerDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<RepositoryHostConnectionProfile>> ListAsync(
        CancellationToken cancellationToken)
    {
        var entities = await _db.RepositoryHostConnections
            .AsNoTracking()
            .OrderBy(x => x.Key)
            .ToListAsync(cancellationToken);

        return entities.Select(MapToProfile).ToList();
    }

    public async Task<RepositoryHostConnectionProfile?> GetByKeyAsync(
        string key,
        CancellationToken cancellationToken)
    {
        var entity = await _db.RepositoryHostConnections
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Key == key, cancellationToken);

        return entity is null ? null : MapToProfile(entity);
    }

    public async Task<bool> CreateAsync(
        RepositoryHostConnectionProfile profile,
        CancellationToken cancellationToken)
    {
        if (await _db.RepositoryHostConnections.AnyAsync(
                x => x.Key == profile.Key,
                cancellationToken))
        {
            return false;
        }

        var entity = MapToEntity(profile);
        _db.RepositoryHostConnections.Add(entity);

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
        RepositoryHostConnectionProfile profile,
        CancellationToken cancellationToken)
    {
        var entity = await _db.RepositoryHostConnections.FindAsync(
            [profile.Key],
            cancellationToken);

        if (entity is null)
        {
            return false;
        }

        entity.DisplayName = profile.DisplayName;
        entity.Enabled = profile.Enabled;
        entity.Provider = profile.Provider;
        entity.OrganizationUrl = profile.OrganizationUrl;
        entity.Project = profile.Project;
        entity.PersonalAccessTokenSecretName = profile.PersonalAccessTokenReference.Name;
        entity.UpdatedAt = profile.UpdatedAt;

        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> DeleteAsync(
        string key,
        CancellationToken cancellationToken)
    {
        var entity = await _db.RepositoryHostConnections.FindAsync(
            [key],
            cancellationToken);

        if (entity is null)
        {
            return false;
        }

        _db.RepositoryHostConnections.Remove(entity);
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static RepositoryHostConnectionEntity MapToEntity(
        RepositoryHostConnectionProfile profile)
    {
        return new RepositoryHostConnectionEntity
        {
            Key = profile.Key,
            DisplayName = profile.DisplayName,
            Enabled = profile.Enabled,
            Provider = profile.Provider,
            OrganizationUrl = profile.OrganizationUrl,
            Project = profile.Project,
            PersonalAccessTokenSecretName = profile.PersonalAccessTokenReference.Name,
            CreatedAt = profile.CreatedAt,
            UpdatedAt = profile.UpdatedAt,
        };
    }

    private static RepositoryHostConnectionProfile MapToProfile(
        RepositoryHostConnectionEntity entity)
    {
        return new RepositoryHostConnectionProfile
        {
            Key = entity.Key,
            DisplayName = entity.DisplayName,
            Enabled = entity.Enabled,
            Provider = entity.Provider,
            OrganizationUrl = entity.OrganizationUrl,
            Project = entity.Project,
            PersonalAccessTokenReference =
                string.IsNullOrWhiteSpace(entity.PersonalAccessTokenSecretName)
                    ? Domain.Secrets.SecretReference.Empty
                    : Domain.Secrets.SecretReference.ByName(entity.PersonalAccessTokenSecretName),
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

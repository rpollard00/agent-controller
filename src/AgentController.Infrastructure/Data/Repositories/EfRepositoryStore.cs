using System.Text.Json;
using AgentController.Application;
using AgentController.Domain;
using AgentController.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace AgentController.Infrastructure.Data.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IRepositoryStore"/> using SQLite.
/// Supports reading and upserting cached repository profiles.
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

    public async Task<RepositoryProfile?> GetByKeyAsync(
        string key,
        CancellationToken cancellationToken)
    {
        var entity = await _db.Repositories.FindAsync([key], cancellationToken);
        return entity is null ? null : MapToProfile(entity);
    }

    public async Task UpsertAsync(
        RepositoryProfile profile,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var existing = await _db.Repositories.FindAsync([profile.Key], cancellationToken);

        if (existing is not null)
        {
            existing.CloneUrl = profile.CloneUrl;
            existing.DefaultBranch = profile.DefaultBranch;
            existing.EnvironmentProfile = profile.EnvironmentProfile;
            existing.RuntimeProfile = profile.RuntimeProfile;
            existing.AllowedPathsJson = SerializeList(profile.AllowedPaths);
            existing.UpdatedAt = now;
        }
        else
        {
            var entity = new RepositoryEntity
            {
                Key = profile.Key,
                CloneUrl = profile.CloneUrl,
                DefaultBranch = profile.DefaultBranch,
                EnvironmentProfile = profile.EnvironmentProfile,
                RuntimeProfile = profile.RuntimeProfile,
                AllowedPathsJson = SerializeList(profile.AllowedPaths),
                CreatedAt = now,
                UpdatedAt = now,
            };
            _db.Repositories.Add(entity);
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    private static RepositoryProfile MapToProfile(RepositoryEntity entity)
    {
        return new RepositoryProfile
        {
            Key = entity.Key,
            CloneUrl = entity.CloneUrl,
            DefaultBranch = entity.DefaultBranch,
            EnvironmentProfile = entity.EnvironmentProfile,
            RuntimeProfile = entity.RuntimeProfile,
            AllowedPaths = DeserializeList(entity.AllowedPathsJson),
        };
    }

    private static string? SerializeList(IReadOnlyList<string>? list)
    {
        if (list is not { Count: > 0 }) return null;
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
}

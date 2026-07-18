using System.Collections.Concurrent;

namespace AgentController.Domain.Secrets;

/// <summary>
/// In-memory test double implementing both <see cref="ISecretStore"/> and <see cref="ISecretManager"/>.
/// 
/// Useful for unit tests that need to verify consumer behavior without a real
/// database or encryption provider. Not thread-safe for concurrent mutations
/// but safe for typical single-threaded test scenarios.
/// </summary>
public sealed class InMemorySecretStore : ISecretStore, ISecretManager
{
    private sealed class SecretEntry
    {
        public string Name { get; init; } = string.Empty;
        public DateTimeOffset CreatedAt { get; init; }
        public List<SecretVersionEntry> Versions { get; init; } = new();
    }

    private sealed class SecretVersionEntry
    {
        public int Version { get; init; }
        public string Value { get; init; } = string.Empty;
        public DateTimeOffset CreatedAt { get; init; }
    }

    private readonly ConcurrentDictionary<string, SecretEntry> _secrets = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task<string?> ResolveAsync(
        string name,
        int? version = null,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_secrets.TryGetValue(name, out var entry))
        {
            return Task.FromResult<string?>(null);
        }

        var target = version.HasValue
            ? entry.Versions.Find(v => v.Version == version.Value)
            : entry.Versions.LastOrDefault();

        return Task.FromResult<string?>(target?.Value);
    }

    /// <inheritdoc />
    public Task<bool> CreateAsync(
        string name,
        string value,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        var now = DateTimeOffset.UtcNow;
        var entry = new SecretEntry
        {
            Name = name,
            CreatedAt = now,
        };

        entry.Versions.Add(new SecretVersionEntry
        {
            Version = 1,
            Value = value,
            CreatedAt = now,
        });

        var added = _secrets.TryAdd(name, entry);
        return Task.FromResult(added);
    }

    /// <inheritdoc />
    public Task<int?> CreateVersionAsync(
        string name,
        string value,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_secrets.TryGetValue(name, out var entry))
        {
            return Task.FromResult<int?>(null);
        }

        var newVersion = entry.Versions.Count + 1;
        var now = DateTimeOffset.UtcNow;

        entry.Versions.Add(new SecretVersionEntry
        {
            Version = newVersion,
            Value = value,
            CreatedAt = now,
        });

        return Task.FromResult<int?>(newVersion);
    }

    /// <inheritdoc />
    public Task<bool> DeleteAsync(
        string name,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        var removed = _secrets.TryRemove(name, out _);
        return Task.FromResult(removed);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SecretInfo>> ListAsync(
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        var list = _secrets.Values
            .OrderBy(e => e.Name)
            .Select(e =>
            {
                var latest = e.Versions.LastOrDefault();
                return new SecretInfo(
                    Name: e.Name,
                    LatestVersion: latest?.Version ?? 0,
                    CreatedAt: e.CreatedAt,
                    UpdatedAt: latest?.CreatedAt ?? e.CreatedAt
                );
            })
            .ToList();

        return Task.FromResult<IReadOnlyList<SecretInfo>>(list);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SecretVersionInfo>?> ListVersionsAsync(
        string name,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_secrets.TryGetValue(name, out var entry))
        {
            return Task.FromResult<IReadOnlyList<SecretVersionInfo>?>(null);
        }

        var versions = entry.Versions
            .OrderBy(v => v.Version)
            .Select(v => new SecretVersionInfo(
                Version: v.Version,
                CreatedAt: v.CreatedAt
            ))
            .ToList();

        return Task.FromResult<IReadOnlyList<SecretVersionInfo>?>(versions);
    }
}

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
        public string SecretType { get; init; } = Domain.Secrets.SecretType.PersonalAccessToken;
        public DateTimeOffset CreatedAt { get; init; }
        public List<SecretVersionEntry> Versions { get; init; } = new();
    }

    private sealed class SecretVersionEntry
    {
        public int Version { get; init; }
        public SecretPayload Payload { get; init; } = new PersonalAccessTokenPayload { Value = string.Empty };
        public DateTimeOffset CreatedAt { get; init; }
    }

    private readonly ConcurrentDictionary<string, SecretEntry> _secrets = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task<SecretPayload?> ResolveAsync(
        string name,
        int? version = null,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_secrets.TryGetValue(name, out var entry))
        {
            return Task.FromResult<SecretPayload?>(null);
        }

        var target = version.HasValue
            ? entry.Versions.Find(v => v.Version == version.Value)
            : entry.Versions.LastOrDefault();

        return Task.FromResult<SecretPayload?>(target?.Payload);
    }

    /// <inheritdoc />
    public Task<bool> CreateAsync(
        string name,
        SecretPayload payload,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        var now = DateTimeOffset.UtcNow;
        var entry = new SecretEntry
        {
            Name = name,
            SecretType = payload.Type,
            CreatedAt = now,
        };

        entry.Versions.Add(new SecretVersionEntry
        {
            Version = 1,
            Payload = payload,
            CreatedAt = now,
        });

        var added = _secrets.TryAdd(name, entry);
        return Task.FromResult(added);
    }

    /// <inheritdoc />
    public Task<int?> CreateVersionAsync(
        string name,
        SecretPayload payload,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_secrets.TryGetValue(name, out var entry))
        {
            return Task.FromResult<int?>(null);
        }

        // Enforce immutable secret type: the payload's type must match the secret's stored type.
        if (payload.Type != entry.SecretType)
        {
            throw new InvalidOperationException(
                $"Cannot add a '{payload.Type}' version to a '{entry.SecretType}' secret. " +
                $"Secret type is immutable once set.");
        }

        var newVersion = entry.Versions.Count + 1;
        var now = DateTimeOffset.UtcNow;

        entry.Versions.Add(new SecretVersionEntry
        {
            Version = newVersion,
            Payload = payload,
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
                    UpdatedAt: latest?.CreatedAt ?? e.CreatedAt,
                    SecretType: e.SecretType
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
            .Select(v =>
            {
                var publicKey = (v.Payload as SshKeyPayload)?.PublicKey;
                return new SecretVersionInfo(
                    Version: v.Version,
                    CreatedAt: v.CreatedAt,
                    SecretType: entry.SecretType,
                    PublicKey: publicKey
                );
            })
            .ToList();

        return Task.FromResult<IReadOnlyList<SecretVersionInfo>?>(versions);
    }
}

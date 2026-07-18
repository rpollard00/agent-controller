using AgentController.Domain.Secrets;
using AgentController.Infrastructure.Data;
using AgentController.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace AgentController.Infrastructure.Secrets;

/// <summary>
/// DB-backed provider implementing both <see cref="ISecretStore"/> (read) and
/// <see cref="ISecretManager"/> (admin) ports using envelope encryption.
/// 
/// Secrets are stored in the NamedSecrets/SecretVersions tables with:
/// - Per-version AES-256-GCM encrypted values
/// - Per-version DEKs wrapped by a master KEK
/// - Write-only value entry (stored values are never decrypted for display)
/// 
/// Fails fast at construction if the KEK is missing or unreadable.
/// </summary>
internal sealed class DbNamedSecretProvider : ISecretStore, ISecretManager
{
    private readonly AgentControllerDbContext _context;
    private readonly AesGcmEnvelopeEncryption _encryption;

    /// <summary>
    /// Create a new DB-backed secret provider.
    /// </summary>
    /// <param name="context">The EF Core database context.</param>
    /// <param name="kekSource">
    /// The KEK source for envelope encryption.
    /// Construction fails if the KEK is missing or invalid.
    /// </param>
    public DbNamedSecretProvider(
        AgentControllerDbContext context,
        IKeyEncryptionKeySource kekSource)
    {
        _context = context;
        // Fail fast: AesGcmEnvelopeEncryption validates KEK at construction.
        _encryption = new AesGcmEnvelopeEncryption(kekSource);
    }

    // ═══════════════════════════════════════════════════════════
    // ISecretStore (read path) — resolves plaintext by name + version
    // ═══════════════════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<SecretPayload?> ResolveAsync(
        string name,
        int? version = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Query: get the specific version, or the latest if version is omitted.
        var query = _context.SecretVersions
            .Where(v => v.NamedSecret!.Name == name);

        if (version.HasValue)
        {
            query = query.Where(v => v.VersionNumber == version.Value);
        }

        var versionEntity = await query
            .OrderByDescending(v => v.VersionNumber)
            .Select(v => new
            {
                EncryptedValue = v.EncryptedValue!,
                Nonce = v.Nonce!,
                WrappedDek = v.WrappedDek!,
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (versionEntity == null)
        {
            return null;
        }

        var plaintext = _encryption.Decrypt(
            versionEntity.EncryptedValue,
            versionEntity.Nonce,
            versionEntity.WrappedDek);

        // For now, all stored secrets are treated as PAT payloads.
        // Typed SSH-key persistence will be added with secret type migrations.
        return new PersonalAccessTokenPayload { Value = plaintext };
    }

    // ═══════════════════════════════════════════════════════════
    // ISecretManager (admin path) — create, version, list
    // ═══════════════════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<bool> CreateAsync(
        string name,
        SecretPayload payload,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Check for existing secret with this name.
        var existing = await _context.NamedSecrets
            .FirstOrDefaultAsync(s => s.Name == name, cancellationToken);

        if (existing != null)
        {
            return false;
        }

        // For now, only PAT payloads are persisted; typed SSH-key storage
        // will be added with secret type migrations.
        var patPayload = (PersonalAccessTokenPayload)payload;
        var (encryptedValue, nonce, wrappedDek) = _encryption.Encrypt(patPayload.Value);
        var now = DateTimeOffset.UtcNow;

        var entity = new NamedSecretEntity
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name,
            CreatedAt = now,
            Versions = new List<SecretVersionEntity>
            {
                new()
                {
                    Id = Guid.NewGuid().ToString("N"),
                    VersionNumber = 1,
                    EncryptedValue = encryptedValue,
                    Nonce = nonce,
                    WrappedDek = wrappedDek,
                    CreatedAt = now,
                },
            },
        };

        await _context.NamedSecrets.AddAsync(entity, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);

        return true;
    }

    /// <inheritdoc />
    public async Task<int?> CreateVersionAsync(
        string name,
        SecretPayload payload,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var secret = await _context.NamedSecrets
            .Include(s => s.Versions)
            .FirstOrDefaultAsync(s => s.Name == name, cancellationToken);

        if (secret == null)
        {
            return null;
        }

        // For now, only PAT payloads are persisted; typed SSH-key storage
        // will be added with secret type migrations.
        var patPayload = (PersonalAccessTokenPayload)payload;

        var newVersionNumber = secret.Versions
            .Select(v => v.VersionNumber)
            .DefaultIfEmpty(0)
            .Max() + 1;

        var (encryptedValue, nonce, wrappedDek) = _encryption.Encrypt(patPayload.Value);

        var versionEntity = new SecretVersionEntity
        {
            Id = Guid.NewGuid().ToString("N"),
            NamedSecretId = secret.Id,
            VersionNumber = newVersionNumber,
            EncryptedValue = encryptedValue,
            Nonce = nonce,
            WrappedDek = wrappedDek,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        secret.Versions.Add(versionEntity);
        await _context.SaveChangesAsync(cancellationToken);

        return newVersionNumber;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var secret = await _context.NamedSecrets
            .FirstOrDefaultAsync(s => s.Name == name, cancellationToken);

        if (secret == null)
        {
            return false;
        }

        // SecretVersions rows are removed via the configured cascade delete.
        _context.NamedSecrets.Remove(secret);
        await _context.SaveChangesAsync(cancellationToken);

        return true;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SecretInfo>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var secrets = await _context.NamedSecrets
            .Include(s => s.Versions)
            .OrderBy(s => s.Name)
            .ToListAsync(cancellationToken);

        return secrets
            .Select(s =>
            {
                var latestVersion = s.Versions
                    .OrderByDescending(v => v.VersionNumber)
                    .FirstOrDefault();

                // For now, all stored secrets are treated as PAT type.
                // Typed secret-type persistence will be added with secret type migrations.
                return new SecretInfo(
                    Name: s.Name,
                    LatestVersion: latestVersion?.VersionNumber ?? 0,
                    CreatedAt: s.CreatedAt,
                    UpdatedAt: latestVersion?.CreatedAt ?? s.CreatedAt,
                    SecretType: Domain.Secrets.SecretType.PersonalAccessToken
                );
            })
            .ToArray();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SecretVersionInfo>?> ListVersionsAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var secret = await _context.NamedSecrets
            .Include(s => s.Versions)
            .FirstOrDefaultAsync(s => s.Name == name, cancellationToken);

        if (secret == null)
        {
            return null;
        }

        return secret.Versions
            .OrderBy(v => v.VersionNumber)
            .Select(v => new SecretVersionInfo(
                Version: v.VersionNumber,
                CreatedAt: v.CreatedAt,
                SecretType: Domain.Secrets.SecretType.PersonalAccessToken
            ))
            .ToArray();
    }
}

using System.Text.Json;
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
/// Each version's encrypted value is a JSON-serialized typed payload.
/// For PAT secrets the payload is <c>{"value":"..."}</c>;
/// for SSH-key secrets it is <c>{"privateKey":"...","publicKey":"...","passphrase":null|"..."}</c>.
/// Type information is stored on the <see cref="NamedSecretEntity.SecretType"/> column.
/// 
/// Fails fast at construction if the KEK is missing or unreadable.
/// </summary>
internal sealed class DbNamedSecretProvider : ISecretStore, ISecretManager
{
    private readonly AgentControllerDbContext _context;
    private readonly AesGcmEnvelopeEncryption _encryption;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

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

    /// <summary>JSON-serializable intermediate for PAT version payloads.</summary>
    private sealed record PatVersionPayload(string Value);

    /// <summary>JSON-serializable intermediate for SSH-key version payloads.</summary>
    private sealed record SshVersionPayload(string PrivateKey, string PublicKey, string? Passphrase);

    // ═══════════════════════════════════════════════════════════
    // Helpers: serialize / deserialize typed payloads to/from encrypted JSON
    // ═══════════════════════════════════════════════════════════

    private static string SerializePayload(SecretPayload payload) => payload switch
    {
        PersonalAccessTokenPayload pat => JsonSerializer.Serialize(
            new PatVersionPayload(pat.Value), JsonOptions),
        SshKeyPayload ssh => JsonSerializer.Serialize(
            new SshVersionPayload(ssh.PrivateKey, ssh.PublicKey, ssh.Passphrase), JsonOptions),
        _ => throw new ArgumentException($"Unknown payload type: {payload.GetType().Name}")
    };

    private static SecretPayload DeserializePayload(string json, string secretType) => secretType switch
    {
        Domain.Secrets.SecretType.PersonalAccessToken => DeserializePatPayload(json),
        Domain.Secrets.SecretType.SshKey => DeserializeSshPayload(json),
        _ => throw new InvalidOperationException($"Unknown secret type: {secretType}")
    };

    /// <summary>
    /// Deserialize a PAT payload, handling both:
    /// - Modern format: <c>{"value":"..."}</c> (JSON envelope)
    /// - Legacy format: the raw PAT string stored directly by the old DbNamedSecretProvider
    /// </summary>
    private static PersonalAccessTokenPayload DeserializePatPayload(string json)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<PatVersionPayload>(json, JsonOptions);
            if (parsed != null)
            {
                return new PersonalAccessTokenPayload { Value = parsed.Value };
            }
        }
        catch (JsonException)
        {
            // Not valid JSON envelope — fall through to legacy handling below.
        }

        // Legacy fallback: the old DbNamedSecretProvider encrypted the raw PAT value
        // string directly, not as a JSON envelope.
        return new PersonalAccessTokenPayload { Value = json };
    }

    /// <summary>
    /// Deserialize an SSH-key payload from its JSON envelope.
    /// </summary>
    private static SshKeyPayload DeserializeSshPayload(string json)
    {
        var parsed = JsonSerializer.Deserialize<SshVersionPayload>(json, JsonOptions)
            ?? throw new InvalidOperationException("Failed to deserialize SSH key payload.");

        return new SshKeyPayload
        {
            PrivateKey = parsed.PrivateKey,
            PublicKey = parsed.PublicKey,
            Passphrase = parsed.Passphrase,
        };
    }

    /// <summary>
    /// Decrypt a version entity and deserialize the plaintext to the correct typed payload.
    /// </summary>
    private SecretPayload DecryptVersion(SecretVersionEntity versionEntity, string secretType)
    {
        var plaintext = _encryption.Decrypt(
            versionEntity.EncryptedValue,
            versionEntity.Nonce,
            versionEntity.WrappedDek);

        return DeserializePayload(plaintext, secretType);
    }

    /// <summary>
    /// Extract the SSH public key from a version entity, or null if the type is not SSH.
    /// </summary>
    private string? DecryptPublicKey(SecretVersionEntity versionEntity, string secretType)
    {
        if (secretType != Domain.Secrets.SecretType.SshKey)
        {
            return null;
        }

        var payload = (SshKeyPayload)DecryptVersion(versionEntity, secretType);
        return payload.PublicKey;
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

        // Query: get the specific version, or the latest if version is omitted,
        // including the parent secret for the SecretType.
        var query = _context.SecretVersions
            .Include(v => v.NamedSecret)
            .Where(v => v.NamedSecret!.Name == name);

        if (version.HasValue)
        {
            query = query.Where(v => v.VersionNumber == version.Value);
        }

        var versionEntity = await query
            .OrderByDescending(v => v.VersionNumber)
            .FirstOrDefaultAsync(cancellationToken);

        if (versionEntity == null)
        {
            return null;
        }

        return DecryptVersion(versionEntity, versionEntity.NamedSecret!.SecretType);
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

        var json = SerializePayload(payload);
        var (encryptedValue, nonce, wrappedDek) = _encryption.Encrypt(json);
        var now = DateTimeOffset.UtcNow;

        var entity = new NamedSecretEntity
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name,
            SecretType = payload.Type,
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

        // Enforce immutable secret type: the payload's type must match the secret's stored type.
        if (payload.Type != secret.SecretType)
        {
            throw new InvalidOperationException(
                $"Cannot add a '{payload.Type}' version to a '{secret.SecretType}' secret. " +
                $"Secret type is immutable once set.");
        }

        var json = SerializePayload(payload);
        var newVersionNumber = secret.Versions
            .Select(v => v.VersionNumber)
            .DefaultIfEmpty(0)
            .Max() + 1;

        var (encryptedValue, nonce, wrappedDek) = _encryption.Encrypt(json);

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

                return new SecretInfo(
                    Name: s.Name,
                    LatestVersion: latestVersion?.VersionNumber ?? 0,
                    CreatedAt: s.CreatedAt,
                    UpdatedAt: latestVersion?.CreatedAt ?? s.CreatedAt,
                    SecretType: s.SecretType
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
                SecretType: secret.SecretType,
                PublicKey: DecryptPublicKey(v, secret.SecretType)
            ))
            .ToArray();
    }
}

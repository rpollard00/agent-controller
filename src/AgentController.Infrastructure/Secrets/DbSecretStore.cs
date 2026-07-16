using AgentController.Application;
using AgentController.Application.Results;
using AgentController.Domain;
using AgentController.Infrastructure.Data;
using AgentController.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace AgentController.Infrastructure.Secrets;

/// <summary>
/// Optional encryptor for at-rest secret values.
/// Implementations wrap the value before persisting and unwrap after reading.
/// A null protector stores values as plaintext.
/// TODO: key rotation support.
/// </summary>
internal interface ISecretProtector
{
    /// <summary>Protect (encrypt) a plaintext value for at-rest storage.</summary>
    string Protect(string plaintext);

    /// <summary>Unprotect (decrypt) a stored value back to plaintext.</summary>
    string Unprotect(string protectedValue);
}

/// <summary>
/// <see cref="ISecretStore"/> implementation backed by the EF Core <see cref="SecretEntity"/> table.
/// Supports encrypted-at-rest storage when an <see cref="ISecretProtector"/> is configured.
/// </summary>
internal sealed class DbSecretStore : ISecretStore
{
    private const string DbKind = "Db";
    private readonly AgentControllerDbContext _context;
    private readonly ISecretProtector? _protector;

    public DbSecretStore(
        AgentControllerDbContext context,
        ISecretProtector? protector = null
    )
    {
        _context = context;
        _protector = protector;
    }

    /// <inheritdoc />
    public async Task<string?> ResolveAsync(
        SecretReference reference,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (reference.Kind != DbKind)
        {
            // This store only handles Db references.
            return null;
        }

        var entity = await _context.Secrets
            .FirstOrDefaultAsync(x => x.Id == reference.Id, cancellationToken);

        if (entity == null)
        {
            return null;
        }

        var value = _protector != null
            ? _protector.Unprotect(entity.Value)
            : entity.Value;

        return value;
    }

    /// <inheritdoc />
    public async Task<SecretWriteResult> WriteAsync(
        SecretReference reference,
        string value,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (reference.Kind != DbKind)
        {
            return SecretWriteResult.FailureResult(
                $"DbSecretStore only handles Kind '{DbKind}', got '{reference.Kind}'."
            );
        }

        var protectedValue = _protector != null
            ? _protector.Protect(value)
            : value;

        var existing = await _context.Secrets
            .FirstOrDefaultAsync(x => x.Id == reference.Id, cancellationToken);

        if (existing != null)
        {
            existing.Value = protectedValue;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
        }
        else
        {
            var now = DateTimeOffset.UtcNow;
            await _context.Secrets.AddAsync(new SecretEntity
            {
                Id = reference.Id,
                Value = protectedValue,
                CreatedAt = now,
                UpdatedAt = now,
            }, cancellationToken);
        }

        await _context.SaveChangesAsync(cancellationToken);

        return SecretWriteResult.SuccessResult(
            metadata: existing != null ? "updated" : "created"
        );
    }
}

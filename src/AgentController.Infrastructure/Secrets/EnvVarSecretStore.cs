using AgentController.Application;
using AgentController.Application.Results;
using AgentController.Domain;

namespace AgentController.Infrastructure.Secrets;

/// <summary>
/// <see cref="IManagedSecretStore"/> implementation backed by environment variables.
/// Preserves the existing "ENV:NAME" convention: resolving a SecretReference
/// with Kind "EnvVar" reads <c>Environment.GetEnvironmentVariable(reference.Id)</c>.
///
/// Write operations always fail because environment variables are not
/// writable at runtime through this abstraction.
/// </summary>
internal sealed class EnvVarSecretStore : IManagedSecretStore
{
    private const string EnvVarKind = "EnvVar";

    /// <inheritdoc />
    public Task<string?> ResolveAsync(
        SecretReference reference,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (reference.Kind != EnvVarKind)
        {
            // This store only handles EnvVar references.
            return Task.FromResult<string?>(null);
        }

        var value = Environment.GetEnvironmentVariable(reference.Id);
        return Task.FromResult(value);
    }

    /// <inheritdoc />
    public Task<SecretWriteResult> WriteAsync(
        SecretReference reference,
        string value,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Environment variables are not writable at runtime through this abstraction.
        // Write operations always fail for EnvVar-backed secrets.
        return Task.FromResult(
            SecretWriteResult.FailureResult(
                "Environment variable secrets are read-only; " +
                "use a Db-backed SecretReference for writable secrets."
            )
        );
    }
}

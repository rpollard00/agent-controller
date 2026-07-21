using AgentController.Domain.Secrets;
using Microsoft.Extensions.DependencyInjection;

namespace AgentController.Infrastructure;

/// <summary>
/// Resolves runtime clone credentials from the scoped secret store while allowing the
/// source-control provider itself to remain a singleton.
/// </summary>
internal sealed class RepositoryCloneCredentialResolver(IServiceScopeFactory scopeFactory)
{
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;

    /// <summary>
    /// Resolves an optionally version-pinned secret reference and requires a PAT payload.
    /// Secret values are returned only to the clone process setup and are never logged.
    /// </summary>
    public async Task<string> ResolvePersonalAccessTokenAsync(
        SecretReference reference,
        CancellationToken cancellationToken
    )
    {
        var payload = await ResolveAsync(reference, "HTTPS", "PAT", cancellationToken);

        if (payload is not PersonalAccessTokenPayload pat)
        {
            throw new InvalidOperationException(
                $"Secret '{reference.Name}' is not a personal-access-token secret."
            );
        }

        if (string.IsNullOrWhiteSpace(pat.Value))
        {
            throw new InvalidOperationException(
                $"PAT secret '{reference.Name}' contains an empty token."
            );
        }

        return pat.Value;
    }

    /// <summary>
    /// Resolves an optionally version-pinned secret reference and requires an SSH-key payload.
    /// </summary>
    public async Task<SshKeyPayload> ResolveSshKeyAsync(
        SecretReference reference,
        CancellationToken cancellationToken
    )
    {
        var payload = await ResolveAsync(reference, "SSH", "SSH-key", cancellationToken);

        if (payload is not SshKeyPayload sshKey)
        {
            throw new InvalidOperationException(
                $"Secret '{reference.Name}' is not an SSH-key secret."
            );
        }

        if (string.IsNullOrWhiteSpace(sshKey.PrivateKey))
        {
            throw new InvalidOperationException(
                $"SSH-key secret '{reference.Name}' contains an empty private key."
            );
        }

        return sshKey;
    }

    private async Task<SecretPayload> ResolveAsync(
        SecretReference reference,
        string transport,
        string credentialType,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(reference);

        if (!reference.IsSpecified)
        {
            throw new InvalidOperationException(
                $"{transport} clone credentials do not specify a {credentialType} secret."
            );
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var secretStore = scope.ServiceProvider.GetService<ISecretStore>();
        if (secretStore is null)
        {
            throw new InvalidOperationException(
                $"{transport} clone credentials cannot be resolved because no secret store is configured."
            );
        }

        var payload = await secretStore.ResolveAsync(
            reference.Name,
            reference.Version,
            cancellationToken
        );

        if (payload is null)
        {
            var version = reference.Version is null ? "latest version" : $"version {reference.Version}";
            throw new InvalidOperationException(
                $"{credentialType} secret '{reference.Name}' ({version}) was not found."
            );
        }

        return payload;
    }
}

using AgentController.Application;
using AgentController.Domain;

namespace AgentController.Infrastructure;

/// <summary>
/// Shared helper for resolving Azure DevOps Personal Access Tokens.
/// Routes resolution through <see cref="Domain.Secrets.ISecretStore"/> for work-source
/// profiles and through <see cref="IManagedSecretStore"/> for legacy managed profiles.
///
/// Used by both the work-source (Boards) and repo-host (Repos) ADO paths
/// so they share the same resolution logic.
/// </summary>
internal sealed class AzureDevOpsPatResolver(
    IManagedSecretStore managedSecretStore,
    Domain.Secrets.ISecretStore secretStore)
{
    /// <summary>
    /// Resolves a PAT from a <see cref="Domain.Secrets.SecretReference"/> (named + versioned)
    /// via <see cref="Domain.Secrets.ISecretStore"/>.
    /// </summary>
    public Task<string?> ResolveFromSecretReferenceAsync(
        Domain.Secrets.SecretReference reference,
        CancellationToken cancellationToken
    )
    {
        if (!reference.IsSpecified)
        {
            return Task.FromResult<string?>(null);
        }

        return secretStore.ResolveAsync(
            reference.Name,
            reference.Version,
            cancellationToken);
    }

    /// <summary>
    /// Resolves a PAT from a legacy <see cref="SecretReference"/> via <see cref="IManagedSecretStore"/>.
    /// </summary>
    public Task<string?> ResolveAsync(
        SecretReference reference,
        CancellationToken cancellationToken
    )
    {
        return managedSecretStore.ResolveAsync(reference, cancellationToken);
    }

    /// <summary>
    /// Resolves a PAT from a direct PAT value.
    /// Returns the value as-is when non-empty; returns null for empty/whitespace input.
    /// </summary>
    /// <param name="patValue">
    /// A direct PAT value.
    /// </param>
    public static Task<string?> ResolveFromLegacyValueAsync(
        string patValue,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(patValue))
        {
            return Task.FromResult<string?>(null);
        }

        // Direct PAT value — return as-is.
        return Task.FromResult<string?>(patValue);
    }
}

using AgentController.Domain.Secrets;

namespace AgentController.Infrastructure;

/// <summary>
/// Shared helper for resolving Azure DevOps Personal Access Tokens.
/// Routes resolution through <see cref="ISecretStore"/> for named,
/// envelope-encrypted secrets.
///
/// Used by both the work-source (Boards) and repo-host (Repos) ADO paths
/// so they share the same resolution logic.
/// </summary>
internal sealed class AzureDevOpsPatResolver(
    ISecretStore secretStore)
{
    /// <summary>
    /// Resolves a PAT from a <see cref="SecretReference"/> (named + versioned)
    /// via <see cref="ISecretStore"/>.
    /// </summary>
    public Task<string?> ResolveFromSecretReferenceAsync(
        SecretReference reference,
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

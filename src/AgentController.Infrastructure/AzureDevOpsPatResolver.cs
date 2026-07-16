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
    /// Resolves a PAT from a legacy "ENV:NAME" string or a direct PAT value.
    /// For "ENV:NAME" references, converts to a <c>SecretReference</c> and resolves
    /// through <see cref="IManagedSecretStore"/>. For direct values, returns as-is.
    ///
    /// This provides backward compatibility for the existing <c>ENV:</c> convention
    /// used in <c>appsettings</c> and <c>AzureDevOpsBoardsOptions.PersonalAccessToken</c>.
    /// </summary>
    /// <param name="patValue">
    /// Either a direct PAT value or an "ENV:VARIABLE_NAME" reference.
    /// </param>
    public async Task<string?> ResolveFromLegacyValueAsync(
        string patValue,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(patValue))
        {
            return null;
        }

        const string envPrefix = "ENV:";
        if (patValue.StartsWith(envPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var envName = patValue[envPrefix.Length..].Trim();
            if (string.IsNullOrWhiteSpace(envName))
            {
                return null;
            }

            var reference = SecretReference.EnvironmentVariable(envName);
            return await ResolveAsync(reference, cancellationToken);
        }

        // Direct PAT value — return as-is.
        return patValue;
    }
}

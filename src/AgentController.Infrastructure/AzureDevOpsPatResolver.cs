using AgentController.Application;
using AgentController.Domain;

namespace AgentController.Infrastructure;

/// <summary>
/// Shared helper for resolving Azure DevOps Personal Access Tokens.
/// Routes resolution through <see cref="IManagedSecretStore"/> for managed profiles
/// while providing backward compatibility for legacy "ENV:NAME" and direct PAT forms.
///
/// Used by both the work-source (Boards) and repo-host (Repos) ADO paths
/// so they share the same resolution logic.
/// </summary>
internal sealed class AzureDevOpsPatResolver(IManagedSecretStore secretStore)
{
    /// <summary>
    /// Resolves a PAT from a <see cref="SecretReference"/> via <see cref="IManagedSecretStore"/>.
    /// </summary>
    public Task<string?> ResolveAsync(
        SecretReference reference,
        CancellationToken cancellationToken
    )
    {
        return secretStore.ResolveAsync(reference, cancellationToken);
    }

    /// <summary>
    /// Resolves a PAT from a legacy environment variable name.
    /// Converts the name to a <c>SecretReference</c> of kind "EnvVar" and resolves
    /// through <see cref="IManagedSecretStore"/> (which dispatches to <c>EnvVarSecretStore</c>).
    /// </summary>
    /// <param name="environmentVariableName">
    /// The environment variable name (without "ENV:" prefix).
    /// </param>
    public Task<string?> ResolveFromEnvironmentVariableAsync(
        string environmentVariableName,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(environmentVariableName))
        {
            return Task.FromResult<string?>(null);
        }

        var reference = SecretReference.EnvironmentVariable(environmentVariableName.Trim());
        return ResolveAsync(reference, cancellationToken);
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

            return await ResolveFromEnvironmentVariableAsync(envName, cancellationToken);
        }

        // Direct PAT value — return as-is.
        return patValue;
    }
}

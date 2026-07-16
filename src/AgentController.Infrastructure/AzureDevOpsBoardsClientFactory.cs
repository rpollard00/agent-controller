using AgentController.Application;
using AgentController.Domain;
using Microsoft.Extensions.Logging;

namespace AgentController.Infrastructure;

/// <summary>
/// Creates short-lived Azure DevOps clients from enabled effective profiles.
/// Resolves the PAT through <see cref="IManagedSecretStore"/> using the shared
/// <see cref="AzureDevOpsPatResolver"/>, then delegates client construction
/// to the shared <see cref="AzureDevOpsClientFactory"/>.
/// </summary>
internal sealed class AzureDevOpsBoardsClientFactory(
    AzureDevOpsPatResolver patResolver,
    AzureDevOpsClientFactory clientFactory
) : IAzureDevOpsBoardsClientFactory
{
    /// <summary>
    /// Creates a client for the given profile. PAT resolution is synchronous
    /// because the factory interface is synchronous; the PAT is resolved from
    /// the profile's <c>PatEnvironmentVariable</c> field via the shared resolver.
    /// </summary>
    public IAzureDevOpsBoardsClient Create(WorkSourceEnvironmentProfile profile)
    {
        if (!profile.Enabled)
        {
            throw new InvalidOperationException(
                $"Azure DevOps environment '{profile.Key}' is disabled."
            );
        }

        // Resolve PAT through the shared resolver.
        string? resolvedPat = null;
        if (!string.IsNullOrWhiteSpace(profile.PatEnvironmentVariable))
        {
            resolvedPat = patResolver
                .ResolveFromEnvironmentVariableAsync(
                    profile.PatEnvironmentVariable,
                    CancellationToken.None
                )
                .GetAwaiter()
                .GetResult();
        }

        if (string.IsNullOrWhiteSpace(resolvedPat))
        {
            throw new InvalidOperationException(
                $"PAT could not be resolved for Azure DevOps environment '{profile.Key}'."
            );
        }

        // Delegate to the shared client factory.
        return clientFactory.Create(
            profile.OrganizationUrl,
            profile.Project,
            resolvedPat
        );
    }
}

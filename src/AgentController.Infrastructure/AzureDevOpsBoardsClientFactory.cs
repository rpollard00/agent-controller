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
    /// the profile's <see cref="WorkSourceEnvironmentProfile.PersonalAccessTokenReference"/>
    /// via <see cref="Domain.Secrets.ISecretStore"/>.
    /// </summary>
    public IAzureDevOpsBoardsClient Create(WorkSourceEnvironmentProfile profile)
    {
        if (!profile.Enabled)
        {
            throw new InvalidOperationException(
                $"Azure DevOps environment '{profile.Key}' is disabled."
            );
        }

        string? resolvedPat = patResolver
            .ResolveFromSecretReferenceAsync(
                profile.PersonalAccessTokenReference,
                CancellationToken.None
            )
            .GetAwaiter()
            .GetResult();

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

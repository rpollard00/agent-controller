using AgentController.Application;
using AgentController.Domain;

namespace AgentController.Infrastructure;

/// <summary>
/// Creates <see cref="IAzureDevOpsBoardsClient"/> instances from resolved work-source environments.
/// Derives OrganizationUrl and PAT from the referenced <see cref="ConnectionProfile"/>'s
/// <see cref="AzureDevOpsConnectionSettings"/> and Project from the consumer profile.
/// </summary>
internal sealed class AzureDevOpsBoardsClientFactory(
    AzureDevOpsClientFactory clientFactory,
    AzureDevOpsPatResolver patResolver
) : IAzureDevOpsBoardsClientFactory
{
    public async Task<IAzureDevOpsBoardsClient> CreateAsync(
        ResolvedWorkSourceEnvironment resolved,
        CancellationToken cancellationToken
    )
    {
        var connection = resolved.Connection;
        if (connection is null)
        {
            throw new InvalidOperationException(
                "Cannot create Azure DevOps boards client: resolved environment has no ConnectionProfile. " +
                "Ensure the work-source environment references a valid managed connection.");
        }

        var settings = connection.ProviderSettings as AzureDevOpsConnectionSettings;
        if (settings is null)
        {
            throw new InvalidOperationException(
                $"Cannot create Azure DevOps boards client: connection '{connection.Key}' " +
                $"has provider '{connection.Provider}' but no AzureDevOpsConnectionSettings.");
        }

        var organizationUrl = settings.OrganizationUrl;
        if (string.IsNullOrWhiteSpace(organizationUrl))
        {
            throw new InvalidOperationException(
                $"Cannot create Azure DevOps boards client: connection '{connection.Key}' " +
                "has no OrganizationUrl configured.");
        }

        // Resolve PAT from the connection's secret reference.
        var pat = await patResolver.ResolveFromSecretReferenceAsync(
            settings.PersonalAccessTokenReference,
            cancellationToken
        );

        if (string.IsNullOrWhiteSpace(pat))
        {
            throw new InvalidOperationException(
                $"Cannot create Azure DevOps boards client: PAT could not be resolved " +
                $"for connection '{connection.Key}'.");
        }

        // Project comes from the consumer profile, not the connection.
        var project = resolved.Profile.Project;

        return clientFactory.Create(organizationUrl, project, pat);
    }
}

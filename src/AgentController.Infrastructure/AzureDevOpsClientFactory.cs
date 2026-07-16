using AgentController.Application;
using AgentController.Infrastructure.Options;
using Microsoft.Extensions.Logging;

namespace AgentController.Infrastructure;

/// <summary>
/// Shared factory for constructing authenticated <see cref="IAzureDevOpsBoardsClient"/>
/// instances from organization URL, project, and resolved PAT.
///
/// Used by both the work-source (Boards) and repo-host (Repos) ADO paths
/// so they share one authenticated ADO client construction path.
/// </summary>
internal sealed class AzureDevOpsClientFactory(ILoggerFactory loggerFactory)
{
    /// <summary>
    /// Create an authenticated ADO client from connection parameters.
    /// </summary>
    /// <param name="organizationUrl">The Azure DevOps organization URL.</param>
    /// <param name="project">The Azure DevOps project name.</param>
    /// <param name="personalAccessToken">The resolved PAT value (already resolved through IManagedSecretStore).</param>
    public IAzureDevOpsBoardsClient Create(
        string organizationUrl,
        string project,
        string personalAccessToken
    )
    {
        var options = new AzureDevOpsBoardsOptions
        {
            BaseUrl = organizationUrl,
            Project = project,
            PersonalAccessToken = string.Empty, // PAT injected via constructor override
        };

        var logger = loggerFactory.CreateLogger<AzureDevOpsBoardsClient>();

        return new AzureDevOpsBoardsClient(
            new HttpClient(),
            options,
            logger,
            personalAccessToken: personalAccessToken
        );
    }
}

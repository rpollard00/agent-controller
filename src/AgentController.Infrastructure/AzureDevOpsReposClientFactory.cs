using AgentController.Application;
using AgentController.Domain;
using AgentController.Infrastructure.Options;
using Microsoft.Extensions.Logging;


namespace AgentController.Infrastructure;

/// <summary>
/// Creates short-lived <see cref="IAzureDevOpsBoardsClient"/> instances from
/// <see cref="RepositoryHostConnectionProfile"/> entries. Resolves the PAT
/// through <see cref="ISecretStore"/> instead of reading environment variables
/// directly.
/// </summary>
internal sealed class AzureDevOpsReposClientFactory(
    ILoggerFactory loggerFactory
) : IAzureDevOpsReposClientFactory
{
    /// <summary>
    /// Create an authenticated ADO client for the given repository host connection profile.
    /// The PAT is resolved via <see cref="ISecretStore"/> from the profile's
    /// <see cref="RepositoryHostConnectionProfile.PersonalAccessTokenReference"/>.
    /// </summary>
    public IAzureDevOpsBoardsClient Create(
        RepositoryHostConnectionProfile profile,
        string personalAccessToken
    )
    {
        if (!profile.Enabled)
        {
            throw new InvalidOperationException(
                $"Repository host connection '{profile.Key}' is disabled."
            );
        }

        var options = new AzureDevOpsBoardsOptions
        {
            BaseUrl = profile.OrganizationUrl,
            Project = profile.Project,
            PersonalAccessToken = personalAccessToken,
        };

        return new AzureDevOpsBoardsClient(
            new HttpClient(),
            options,
            loggerFactory.CreateLogger<AzureDevOpsBoardsClient>()
        );
    }
}

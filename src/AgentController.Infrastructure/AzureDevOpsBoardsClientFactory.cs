using AgentController.Application;
using AgentController.Domain;
using AgentController.Infrastructure.Options;
using Microsoft.Extensions.Logging;

namespace AgentController.Infrastructure;

/// <summary>Creates short-lived Azure DevOps clients from enabled effective profiles.</summary>
internal sealed class AzureDevOpsBoardsClientFactory(ILoggerFactory loggerFactory)
    : IAzureDevOpsBoardsClientFactory
{
    public IAzureDevOpsBoardsClient Create(AzureDevOpsEnvironmentProfile profile)
    {
        if (!profile.Enabled)
        {
            throw new InvalidOperationException(
                $"Azure DevOps environment '{profile.Key}' is disabled."
            );
        }

        var options = new AzureDevOpsBoardsOptions
        {
            BaseUrl = profile.OrganizationUrl,
            Project = profile.Project,
            PersonalAccessToken = string.IsNullOrWhiteSpace(profile.PatEnvironmentVariable)
                ? string.Empty
                : $"ENV:{profile.PatEnvironmentVariable}",
        };

        return new AzureDevOpsBoardsClient(
            new HttpClient(),
            options,
            loggerFactory.CreateLogger<AzureDevOpsBoardsClient>()
        );
    }
}

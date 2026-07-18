using AgentController.Application;
using AgentController.Infrastructure.Options;
using Microsoft.Extensions.Logging;

namespace AgentController.Infrastructure;

/// <summary>
/// Default concrete implementation of <see cref="AzureDevOpsClientFactory"/>.
/// </summary>
internal sealed class DefaultAzureDevOpsClientFactory(ILoggerFactory loggerFactory)
    : AzureDevOpsClientFactory
{
    public override IAzureDevOpsBoardsClient Create(
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

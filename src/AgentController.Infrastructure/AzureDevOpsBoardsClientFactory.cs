using AgentController.Application;
using AgentController.Domain;
using AgentController.Infrastructure.Options;
using Microsoft.Extensions.Logging;

namespace AgentController.Infrastructure;

/// <summary>
/// Creates short-lived Azure DevOps clients from enabled effective profiles.
/// Resolves the PAT through <see cref="ISecretStore"/> using the shared
/// <see cref="AzureDevOpsPatResolver"/> so both work-source (Boards) and
/// repo-host (Repos) ADO paths share the same resolution helper.
/// </summary>
internal sealed class AzureDevOpsBoardsClientFactory(
    ILoggerFactory loggerFactory,
    AzureDevOpsPatResolver patResolver
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
        // The profile's PatEnvironmentVariable is an environment variable name
        // (without "ENV:" prefix), so we use ResolveFromEnvironmentVariableAsync.
        string? resolvedPat = null;
        if (!string.IsNullOrWhiteSpace(profile.PatEnvironmentVariable))
        {
            // Synchronous resolution for the factory interface.
            // The resolver dispatches through ISecretStore which handles EnvVar kind.
            resolvedPat = patResolver
                .ResolveFromEnvironmentVariableAsync(
                    profile.PatEnvironmentVariable,
                    CancellationToken.None
                )
                .GetAwaiter()
                .GetResult();
        }

        var options = new AzureDevOpsBoardsOptions
        {
            BaseUrl = profile.OrganizationUrl,
            Project = profile.Project,
            PersonalAccessToken = string.Empty,
        };

        return new AzureDevOpsBoardsClient(
            new HttpClient(),
            options,
            loggerFactory.CreateLogger<AzureDevOpsBoardsClient>(),
            personalAccessToken: resolvedPat
        );
    }
}

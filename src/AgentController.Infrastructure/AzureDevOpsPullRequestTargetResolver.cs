using AgentController.Application;
using AgentController.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace AgentController.Infrastructure;

/// <summary>
/// Resolves a canonical pull request to detached Azure DevOps repository coordinates and
/// credentials. The returned target is short-lived and must never be logged or persisted.
/// </summary>
internal static class AzureDevOpsPullRequestTargetResolver
{
    public static async Task<AzureDevOpsPullRequestTarget> ResolveAsync(
        IServiceScopeFactory scopeFactory,
        PullRequestReference pullRequest,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(pullRequest);
        cancellationToken.ThrowIfCancellationRequested();

        await using var scope = scopeFactory.CreateAsyncScope();
        var repositoryStore = scope.ServiceProvider.GetRequiredService<IRepositoryStore>();
        var connectionStore = scope.ServiceProvider.GetRequiredService<IConnectionStore>();
        var patResolver = scope.ServiceProvider.GetRequiredService<AzureDevOpsPatResolver>();

        var repository = await repositoryStore.GetByKeyAsync(
            pullRequest.RepositoryKey.Trim(),
            cancellationToken
        ) ?? throw new InvalidOperationException(
            $"Managed repository '{pullRequest.RepositoryKey.Trim()}' was not found."
        );

        var connectionKey = Clean(repository.RepositoryHostConnectionKey);
        if (connectionKey.Length == 0
            || !connectionKey.Equals(
                pullRequest.EnvironmentKey.Trim(),
                StringComparison.OrdinalIgnoreCase
            ))
        {
            throw new InvalidOperationException(
                $"Managed repository '{repository.Key}' is not owned by connection "
                    + $"'{pullRequest.EnvironmentKey.Trim()}'."
            );
        }

        var connection = await connectionStore.GetByKeyAsync(
            connectionKey,
            cancellationToken
        );
        if (connection is null
            || !connection.Enabled
            || !connection.Provider.Equals("AzureDevOps", StringComparison.OrdinalIgnoreCase)
            || connection.ProviderSettings is not AzureDevOpsConnectionSettings settings)
        {
            throw new InvalidOperationException(
                $"Azure DevOps connection '{connectionKey}' is unavailable."
            );
        }

        var organizationUrl = Clean(settings.OrganizationUrl);
        var project = Clean(repository.Project);
        var remoteIdentity = ResolveRemoteIdentity(repository);
        if (!IsSupportedOrganizationUrl(organizationUrl)
            || project.Length == 0
            || remoteIdentity.Length == 0
            || !settings.PersonalAccessTokenReference.IsSpecified)
        {
            throw new InvalidOperationException(
                $"Managed repository '{repository.Key}' does not have complete Azure DevOps coordinates."
            );
        }

        string? personalAccessToken;
        try
        {
            personalAccessToken = await patResolver.ResolveFromSecretReferenceAsync(
                settings.PersonalAccessTokenReference,
                cancellationToken
            );
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            throw new InvalidOperationException(
                $"The Azure DevOps credential for connection '{connectionKey}' could not be resolved."
            );
        }

        if (string.IsNullOrWhiteSpace(personalAccessToken))
        {
            throw new InvalidOperationException(
                $"The Azure DevOps credential for connection '{connectionKey}' is unavailable."
            );
        }

        return new AzureDevOpsPullRequestTarget(
            new AzureDevOpsManagedRepository
            {
                EnvironmentKey = connection.Key.Trim(),
                RepositoryKey = repository.Key.Trim(),
                Project = project,
                RemoteIdentity = remoteIdentity,
                RepositoryWebUrl = CleanOrNull(repository.WebUrl),
            },
            organizationUrl,
            personalAccessToken
        );
    }

    private static string ResolveRemoteIdentity(RepositoryProfile repository)
    {
        var explicitIdentity = Clean(repository.RemoteIdentity);
        if (explicitIdentity.Length > 0)
        {
            return explicitIdentity;
        }

        return TryReadRepositoryName(repository.WebUrl)
            ?? TryReadRepositoryName(repository.CloneUrl)
            ?? string.Empty;
    }

    private static string? TryReadRepositoryName(string? repositoryUrl)
    {
        if (!Uri.TryCreate(repositoryUrl, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var segments = uri.Segments;
        for (var index = 0; index < segments.Length - 1; index++)
        {
            if (!segments[index].Equals("_git/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var name = Uri.UnescapeDataString(segments[index + 1].TrimEnd('/'));
            if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            {
                name = name[..^4];
            }

            return name.Length == 0 ? null : name;
        }

        return null;
    }

    private static bool IsSupportedOrganizationUrl(string organizationUrl)
    {
        return Uri.TryCreate(organizationUrl, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);
    }

    private static string Clean(string? value) => value?.Trim() ?? string.Empty;

    private static string? CleanOrNull(string? value)
    {
        var cleaned = Clean(value);
        return cleaned.Length == 0 ? null : cleaned;
    }
}

/// <summary>Resolved Azure DevOps destination for one managed pull request.</summary>
internal sealed record AzureDevOpsPullRequestTarget(
    AzureDevOpsManagedRepository Repository,
    string OrganizationUrl,
    string PersonalAccessToken
);

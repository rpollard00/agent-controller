using System.Collections.Concurrent;
using AgentController.Application;
using AgentController.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentController.Infrastructure;

/// <summary>
/// Discovers active pull requests from every managed repository backed by an enabled
/// Azure DevOps connection. Profile and secret access is scoped to each discovery pass;
/// only detached repository coordinates are used by concurrent HTTP operations.
/// </summary>
internal sealed partial class AzureDevOpsManagedPullRequestDiscovery(
    IServiceScopeFactory scopeFactory,
    AzureDevOpsReposPullRequestClientFactory clientFactory,
    ILogger<AzureDevOpsManagedPullRequestDiscovery> logger
) : IManagedPullRequestDiscovery, IManagedPullRequestDiagnosticDiscovery
{
    private const int MaximumConcurrentRepositories = 4;

    public async Task<IReadOnlyList<ManagedPullRequestSnapshot>> ListActiveAsync(
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        var targets = await ResolveTargetsAsync(cancellationToken);
        if (targets.Count == 0)
        {
            return [];
        }

        var discovered = new ConcurrentBag<ManagedPullRequestSnapshot>();
        await Parallel.ForEachAsync(
            targets,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = MaximumConcurrentRepositories,
            },
            async (target, token) =>
            {
                try
                {
                    using var client = clientFactory.Create(
                        target.OrganizationUrl,
                        target.PersonalAccessToken
                    );
                    var snapshots = await client.ListActiveAsync(target.Repository, token);
                    foreach (var snapshot in snapshots)
                    {
                        if (snapshot.PullRequest.HasCanonicalIdentity)
                        {
                            discovered.Add(snapshot);
                        }
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception)
                {
                    // Deliberately omit exception detail: provider failures may contain
                    // transport internals and discovery must never risk logging credentials.
                    Log.RepositoryDiscoveryFailed(
                        logger,
                        target.Repository.RepositoryKey
                    );
                }
            }
        );

        return discovered
            .GroupBy(snapshot => snapshot.CanonicalKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(snapshot => snapshot.CanonicalKey, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<ManagedPullRequestDiscoveryPage> ListAsync(
        ManagedPullRequestDiscoveryQuery query,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Page < 1)
            throw new ArgumentOutOfRangeException(nameof(query), "Page must be at least one.");
        if (query.PageSize < 1
            || query.PageSize > ManagedPullRequestDiscoveryQuery.MaximumPageSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(query),
                "Page size must be between 1 and 100."
            );
        }

        cancellationToken.ThrowIfCancellationRequested();
        var failures = new ConcurrentBag<ManagedPullRequestDiscoveryFailure>();
        var targets = await ResolveTargetsAsync(
            cancellationToken,
            failures,
            query.SourceControlEnvironmentKey
        );
        var discovered = new ConcurrentBag<ManagedPullRequestSnapshot>();

        await Parallel.ForEachAsync(
            targets,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = MaximumConcurrentRepositories,
            },
            async (target, token) =>
            {
                try
                {
                    using var client = clientFactory.Create(
                        target.OrganizationUrl,
                        target.PersonalAccessToken
                    );
                    var snapshots = await client.ListForDiagnosticsAsync(
                        target.Repository,
                        query.IncludeInactive,
                        token
                    );
                    foreach (var snapshot in snapshots)
                    {
                        if (snapshot.PullRequest.HasCanonicalIdentity)
                        {
                            discovered.Add(snapshot);
                        }
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception)
                {
                    failures.Add(ToFailure(target.Repository));
                    Log.RepositoryDiscoveryFailed(logger, target.Repository.RepositoryKey);
                }
            }
        );

        var ordered = discovered
            .GroupBy(snapshot => snapshot.CanonicalKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(snapshot => snapshot.CanonicalKey, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var offset = ((long)query.Page - 1) * query.PageSize;
        var items = offset >= ordered.LongLength
            ? []
            : ordered.Skip((int)offset).Take(query.PageSize).ToArray();

        return new ManagedPullRequestDiscoveryPage
        {
            Items = items,
            Failures = failures
                .GroupBy(
                    failure => $"{failure.SourceControlEnvironmentKey}|{failure.RepositoryKey}",
                    StringComparer.OrdinalIgnoreCase
                )
                .Select(group => group.First())
                .OrderBy(failure => failure.SourceControlEnvironmentKey, StringComparer.OrdinalIgnoreCase)
                .ThenBy(failure => failure.RepositoryKey, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            Page = query.Page,
            PageSize = query.PageSize,
            Total = ordered.Length,
        };
    }

    private async Task<IReadOnlyList<DiscoveryTarget>> ResolveTargetsAsync(
        CancellationToken cancellationToken,
        ConcurrentBag<ManagedPullRequestDiscoveryFailure>? failures = null,
        string? sourceControlEnvironmentKey = null
    )
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var repositoryStore = scope.ServiceProvider.GetRequiredService<IRepositoryStore>();
        var connectionStore = scope.ServiceProvider.GetRequiredService<IConnectionStore>();
        var patResolver = scope.ServiceProvider.GetRequiredService<AzureDevOpsPatResolver>();

        var repositories = await repositoryStore.ListAsync(cancellationToken);
        var connections = await connectionStore.ListAsync(cancellationToken);
        var connectionsByKey = connections
            .GroupBy(connection => connection.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.First(),
                StringComparer.OrdinalIgnoreCase
            );

        var resolvedPats = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var remoteRepositories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var targets = new List<DiscoveryTarget>();

        foreach (var repository in repositories)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var connectionKey = Clean(repository.RepositoryHostConnectionKey);
            if (!string.IsNullOrWhiteSpace(sourceControlEnvironmentKey)
                && !connectionKey.Equals(
                    sourceControlEnvironmentKey.Trim(),
                    StringComparison.OrdinalIgnoreCase
                ))
            {
                continue;
            }

            var project = Clean(repository.Project);
            var remoteIdentity = ResolveRemoteIdentity(repository);
            if (connectionKey.Length == 0
                || project.Length == 0
                || remoteIdentity.Length == 0
                || !connectionsByKey.TryGetValue(connectionKey, out var connection)
                || !IsEnabledAzureDevOpsConnection(connection))
            {
                continue;
            }

            var settings = (AzureDevOpsConnectionSettings)connection.ProviderSettings!;
            var organizationUrl = Clean(settings.OrganizationUrl);
            if (!IsSupportedOrganizationUrl(organizationUrl))
            {
                continue;
            }

            if (!resolvedPats.TryGetValue(connection.Key, out var personalAccessToken))
            {
                personalAccessToken = await ResolvePatAsync(
                    connection,
                    settings,
                    patResolver,
                    cancellationToken
                );
                resolvedPats[connection.Key] = personalAccessToken;
            }

            if (string.IsNullOrWhiteSpace(personalAccessToken))
            {
                failures?.Add(new ManagedPullRequestDiscoveryFailure
                {
                    SourceControlEnvironmentKey = connection.Key.Trim(),
                    RepositoryKey = Clean(repository.Key),
                    Message = "The managed repository could not be queried.",
                });
                continue;
            }

            var remoteKey = string.Join(
                '|',
                connection.Key.Trim(),
                project,
                remoteIdentity
            );
            if (!remoteRepositories.Add(remoteKey))
            {
                continue;
            }

            targets.Add(new DiscoveryTarget(
                new AzureDevOpsManagedRepository
                {
                    EnvironmentKey = connection.Key.Trim(),
                    RepositoryKey = Clean(repository.Key),
                    Project = project,
                    RemoteIdentity = remoteIdentity,
                    RepositoryWebUrl = CleanOrNull(repository.WebUrl),
                },
                organizationUrl,
                personalAccessToken
            ));
        }

        return targets;
    }

    private async Task<string?> ResolvePatAsync(
        ConnectionProfile connection,
        AzureDevOpsConnectionSettings settings,
        AzureDevOpsPatResolver patResolver,
        CancellationToken cancellationToken
    )
    {
        if (!settings.PersonalAccessTokenReference.IsSpecified)
        {
            return null;
        }

        try
        {
            return await patResolver.ResolveFromSecretReferenceAsync(
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
            // Do not include the resolver exception: a custom secret provider could put
            // secret material in its message.
            Log.PatResolutionFailed(logger, connection.Key);
            return null;
        }
    }

    private static bool IsEnabledAzureDevOpsConnection(ConnectionProfile connection)
    {
        return connection.Enabled
            && string.Equals(
                connection.Provider,
                "AzureDevOps",
                StringComparison.OrdinalIgnoreCase
            )
            && connection.ProviderSettings is AzureDevOpsConnectionSettings;
    }

    private static bool IsSupportedOrganizationUrl(string organizationUrl)
    {
        return Uri.TryCreate(organizationUrl, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);
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

    private static ManagedPullRequestDiscoveryFailure ToFailure(
        AzureDevOpsManagedRepository repository
    ) => new()
    {
        SourceControlEnvironmentKey = repository.EnvironmentKey,
        RepositoryKey = repository.RepositoryKey,
        Message = "The managed repository could not be queried.",
    };

    private static string Clean(string? value) => value?.Trim() ?? string.Empty;

    private static string? CleanOrNull(string? value)
    {
        var cleaned = Clean(value);
        return cleaned.Length == 0 ? null : cleaned;
    }

    private sealed class DiscoveryTarget
    {
        public DiscoveryTarget(
            AzureDevOpsManagedRepository repository,
            string organizationUrl,
            string personalAccessToken
        )
        {
            Repository = repository;
            OrganizationUrl = organizationUrl;
            PersonalAccessToken = personalAccessToken;
        }

        public AzureDevOpsManagedRepository Repository { get; }

        public string OrganizationUrl { get; }

        public string PersonalAccessToken { get; }

        public override string ToString() => Repository.RepositoryKey;
    }

    private static partial class Log
    {
        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Could not resolve the Azure DevOps credential for connection '{ConnectionKey}'; its managed repositories will be skipped."
        )]
        public static partial void PatResolutionFailed(
            ILogger logger,
            string connectionKey
        );

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Azure DevOps pull-request discovery failed for managed repository '{RepositoryKey}'."
        )]
        public static partial void RepositoryDiscoveryFailed(
            ILogger logger,
            string repositoryKey
        );
    }
}

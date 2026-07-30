using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AgentController.Application;
using Microsoft.Extensions.Logging;

namespace AgentController.Infrastructure;

/// <summary>Creates authenticated Azure DevOps Repos pull-request clients.</summary>
internal abstract class AzureDevOpsReposPullRequestClientFactory
{
    public abstract AzureDevOpsReposPullRequestClient Create(
        string organizationUrl,
        string personalAccessToken
    );
}

/// <summary>Default HTTP-client factory for Azure DevOps pull-request discovery.</summary>
internal sealed class DefaultAzureDevOpsReposPullRequestClientFactory(
    ILoggerFactory loggerFactory
) : AzureDevOpsReposPullRequestClientFactory
{
    public override AzureDevOpsReposPullRequestClient Create(
        string organizationUrl,
        string personalAccessToken
    )
    {
        return new AzureDevOpsReposPullRequestClient(
            new HttpClient(),
            organizationUrl,
            personalAccessToken,
            loggerFactory.CreateLogger<AzureDevOpsReposPullRequestClient>()
        );
    }
}

/// <summary>
/// Narrow Azure DevOps Repos REST client for active pull-request enumeration and
/// relationship hydration within one organization.
/// </summary>
internal sealed partial class AzureDevOpsReposPullRequestClient : IDisposable
{
    private const int DefaultPageSize = 100;

    private readonly HttpClient _http;
    private readonly ILogger<AzureDevOpsReposPullRequestClient> _logger;
    private readonly string _organizationUrl;
    private readonly int _pageSize;

    public AzureDevOpsReposPullRequestClient(
        HttpClient http,
        string organizationUrl,
        string personalAccessToken,
        ILogger<AzureDevOpsReposPullRequestClient> logger,
        int pageSize = DefaultPageSize
    )
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentException.ThrowIfNullOrWhiteSpace(organizationUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(personalAccessToken);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);

        _http = http;
        _logger = logger;
        _organizationUrl = organizationUrl.TrimEnd('/');
        _pageSize = pageSize;

        _http.BaseAddress = new Uri(_organizationUrl + "/", UriKind.Absolute);
        var authBytes = Encoding.ASCII.GetBytes($":{personalAccessToken}");
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(authBytes)
        );
        _http.DefaultRequestHeaders.Accept.Clear();
        _http.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json")
        );
    }

    public Task<IReadOnlyList<ManagedPullRequestSnapshot>> ListActiveAsync(
        AzureDevOpsManagedRepository repository,
        CancellationToken cancellationToken
    ) => ListAsync(
        repository,
        includeInactive: false,
        failOnProviderError: false,
        cancellationToken
    );

    /// <summary>
    /// Lists pull requests for diagnostics. Unlike polling discovery, repository-level
    /// list failures are surfaced so callers can report an isolated safe failure.
    /// </summary>
    public Task<IReadOnlyList<ManagedPullRequestSnapshot>> ListForDiagnosticsAsync(
        AzureDevOpsManagedRepository repository,
        bool includeInactive,
        CancellationToken cancellationToken
    ) => ListAsync(
        repository,
        includeInactive,
        failOnProviderError: true,
        cancellationToken
    );

    private async Task<IReadOnlyList<ManagedPullRequestSnapshot>> ListAsync(
        AzureDevOpsManagedRepository repository,
        bool includeInactive,
        bool failOnProviderError,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(repository);
        cancellationToken.ThrowIfCancellationRequested();

        var snapshots = new List<ManagedPullRequestSnapshot>();
        var pullRequestIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var skip = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var endpoint = BuildListEndpoint(repository, skip, includeInactive);
            using var response = await _http.GetAsync(endpoint, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                Log.PullRequestListFailed(
                    _logger,
                    repository.RepositoryKey,
                    (int)response.StatusCode
                );
                if (failOnProviderError)
                {
                    throw new HttpRequestException(
                        "The pull-request provider rejected the repository query."
                    );
                }

                return snapshots;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(
                stream,
                cancellationToken: cancellationToken
            );

            if (!document.RootElement.TryGetProperty("value", out var values)
                || values.ValueKind != JsonValueKind.Array)
            {
                if (failOnProviderError)
                {
                    throw new JsonException("The pull-request provider returned an invalid list response.");
                }

                return snapshots;
            }

            var rawPageCount = values.GetArrayLength();

            foreach (var value in values.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();

                var item = AzureDevOpsPullRequestMapper.MapPullRequest(
                    value,
                    repository,
                    _organizationUrl,
                    includeInactive
                );
                if (item is null
                    || !pullRequestIds.Add(item.Snapshot.PullRequestId))
                {
                    continue;
                }

                snapshots.Add(await HydrateRelationshipsAsync(
                    repository,
                    item,
                    failOnProviderError,
                    cancellationToken
                ));
            }

            if (rawPageCount < _pageSize)
            {
                return snapshots;
            }

            skip += rawPageCount;
        }
    }

    public void Dispose() => _http.Dispose();

    private async Task<ManagedPullRequestSnapshot> HydrateRelationshipsAsync(
        AzureDevOpsManagedRepository repository,
        AzureDevOpsPullRequestItem item,
        bool failOnProviderError,
        CancellationToken cancellationToken
    )
    {
        var labels = item.Snapshot.Labels;
        if (!item.HasLabels)
        {
            labels = await FetchLabelsAsync(
                repository,
                item.Snapshot.PullRequestId,
                failOnProviderError,
                cancellationToken
            );
        }

        var linkedWorkItems = item.Snapshot.LinkedWorkItems;
        if (!item.HasLinkedWorkItems)
        {
            linkedWorkItems = await FetchWorkItemsAsync(
                repository,
                item.Snapshot.PullRequestId,
                failOnProviderError,
                cancellationToken
            );
        }

        return item.Snapshot with
        {
            Labels = labels,
            LinkedWorkItems = linkedWorkItems,
        };
    }

    private async Task<IReadOnlyList<string>> FetchLabelsAsync(
        AzureDevOpsManagedRepository repository,
        string pullRequestId,
        bool failOnProviderError,
        CancellationToken cancellationToken
    )
    {
        var endpoint = BuildPullRequestEndpoint(repository, pullRequestId, "labels");
        try
        {
            using var response = await _http.GetAsync(endpoint, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                Log.RelationshipFetchFailed(
                    _logger,
                    "labels",
                    repository.RepositoryKey,
                    pullRequestId,
                    (int)response.StatusCode
                );
                if (failOnProviderError)
                {
                    throw new HttpRequestException(
                        "The pull-request provider rejected relationship hydration."
                    );
                }

                return [];
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(
                stream,
                cancellationToken: cancellationToken
            );
            EnsureRelationshipResponse(document.RootElement, failOnProviderError);
            return AzureDevOpsPullRequestMapper.MapLabelsResponse(document.RootElement);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            Log.RelationshipFetchException(
                _logger,
                "labels",
                repository.RepositoryKey,
                pullRequestId
            );
            if (failOnProviderError)
            {
                throw;
            }

            return [];
        }
    }

    private async Task<IReadOnlyList<PullRequestWorkItemReference>> FetchWorkItemsAsync(
        AzureDevOpsManagedRepository repository,
        string pullRequestId,
        bool failOnProviderError,
        CancellationToken cancellationToken
    )
    {
        var endpoint = BuildPullRequestEndpoint(repository, pullRequestId, "workitems");
        try
        {
            using var response = await _http.GetAsync(endpoint, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                Log.RelationshipFetchFailed(
                    _logger,
                    "work items",
                    repository.RepositoryKey,
                    pullRequestId,
                    (int)response.StatusCode
                );
                if (failOnProviderError)
                {
                    throw new HttpRequestException(
                        "The pull-request provider rejected relationship hydration."
                    );
                }

                return [];
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(
                stream,
                cancellationToken: cancellationToken
            );
            EnsureRelationshipResponse(document.RootElement, failOnProviderError);
            return AzureDevOpsPullRequestMapper.MapWorkItemsResponse(document.RootElement);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            Log.RelationshipFetchException(
                _logger,
                "work items",
                repository.RepositoryKey,
                pullRequestId
            );
            if (failOnProviderError)
            {
                throw;
            }

            return [];
        }
    }

    private static void EnsureRelationshipResponse(
        JsonElement root,
        bool failOnProviderError
    )
    {
        if (failOnProviderError
            && (!root.TryGetProperty("value", out var value)
                || value.ValueKind != JsonValueKind.Array))
        {
            throw new JsonException(
                "The pull-request provider returned an invalid relationship response."
            );
        }
    }

    private string BuildListEndpoint(
        AzureDevOpsManagedRepository repository,
        int skip,
        bool includeInactive
    )
    {
        var status = includeInactive ? "all" : "active";
        return BuildRepositoryEndpoint(repository)
            + $"/pullrequests?searchCriteria.status={status}"
            + "&searchCriteria.includeLinks=true"
            + $"&$top={_pageSize}&$skip={skip}&api-version=7.1";
    }

    private static string BuildPullRequestEndpoint(
        AzureDevOpsManagedRepository repository,
        string pullRequestId,
        string relationship
    )
    {
        return BuildRepositoryEndpoint(repository)
            + $"/pullRequests/{Uri.EscapeDataString(pullRequestId)}"
            + $"/{relationship}?api-version=7.1";
    }

    private static string BuildRepositoryEndpoint(AzureDevOpsManagedRepository repository)
    {
        return $"{Uri.EscapeDataString(repository.Project)}/_apis/git/repositories/"
            + Uri.EscapeDataString(repository.RemoteIdentity);
    }

    private static partial class Log
    {
        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Failed to list pull requests for managed repository '{RepositoryKey}': HTTP {StatusCode}."
        )]
        public static partial void PullRequestListFailed(
            ILogger logger,
            string repositoryKey,
            int statusCode
        );

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Failed to fetch {Relationship} for pull request {PullRequestId} in managed repository '{RepositoryKey}': HTTP {StatusCode}."
        )]
        public static partial void RelationshipFetchFailed(
            ILogger logger,
            string relationship,
            string repositoryKey,
            string pullRequestId,
            int statusCode
        );

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Failed to fetch {Relationship} for pull request {PullRequestId} in managed repository '{RepositoryKey}'."
        )]
        public static partial void RelationshipFetchException(
            ILogger logger,
            string relationship,
            string repositoryKey,
            string pullRequestId
        );
    }
}

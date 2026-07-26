using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AgentController.Application;
using AgentController.Domain;

namespace AgentController.Infrastructure;

/// <summary>Creates authenticated clients for assistance-story relationship resolution.</summary>
internal abstract class AzureDevOpsAssistanceStoryRelationshipClientFactory
{
    public abstract AzureDevOpsAssistanceStoryRelationshipClient Create(
        string organizationUrl,
        string personalAccessToken
    );
}

/// <summary>Default HTTP-client factory for assistance-story relationship resolution.</summary>
internal sealed class DefaultAzureDevOpsAssistanceStoryRelationshipClientFactory
    : AzureDevOpsAssistanceStoryRelationshipClientFactory
{
    public override AzureDevOpsAssistanceStoryRelationshipClient Create(
        string organizationUrl,
        string personalAccessToken
    ) => new(new HttpClient(), organizationUrl, personalAccessToken);
}

/// <summary>
/// Narrow Azure DevOps client that resolves source stories, their common parent, and the
/// canonical pull-request artifact relation used by a new assistance story.
/// </summary>
internal sealed class AzureDevOpsAssistanceStoryRelationshipClient : IDisposable
{
    private const int MaximumWorkItemsPerRequest = 200;
    private const string ParentRelationType = "System.LinkTypes.Hierarchy-Reverse";
    private const string RelatedRelationType = "System.LinkTypes.Related";

    private readonly HttpClient _http;
    private readonly string _organizationUrl;

    public AzureDevOpsAssistanceStoryRelationshipClient(
        HttpClient http,
        string organizationUrl,
        string personalAccessToken
    )
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentException.ThrowIfNullOrWhiteSpace(organizationUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(personalAccessToken);

        _http = http;
        _organizationUrl = organizationUrl.TrimEnd('/');
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

    public async Task<AssistanceStoryRelationships> ResolveAsync(
        AzureDevOpsManagedRepository repository,
        string pullRequestId,
        string? originatingWorkItemId,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(pullRequestId);
        cancellationToken.ThrowIfCancellationRequested();

        var pullRequest = await FetchPullRequestIdentityAsync(
            repository,
            pullRequestId,
            cancellationToken
        );

        var sourceStories = await ResolveSourceStoriesAsync(
            repository,
            pullRequestId,
            Clean(originatingWorkItemId),
            cancellationToken
        );
        var commonParentUrl = FindCommonParentUrl(sourceStories);
        var relations = new List<WorkItemRelation>();

        foreach (var sourceStory in sourceStories)
        {
            relations.Add(new WorkItemRelation
            {
                RelationType = RelatedRelationType,
                Url = sourceStory.Url,
                Attributes = new Dictionary<string, string>
                {
                    ["comment"] = "Source story for assistance request",
                },
            });
        }

        if (commonParentUrl is not null)
        {
            relations.Add(new WorkItemRelation
            {
                RelationType = ParentRelationType,
                Url = commonParentUrl,
                Attributes = new Dictionary<string, string>
                {
                    ["comment"] = "Parent shared by all source stories",
                },
            });
        }

        relations.Add(new WorkItemRelation
        {
            RelationType = "ArtifactLink",
            Url = BuildPullRequestArtifactUri(
                pullRequest.ProjectId,
                pullRequest.RepositoryId,
                pullRequestId
            ),
            Attributes = new Dictionary<string, string>
            {
                ["name"] = "Pull Request",
            },
        });

        return new AssistanceStoryRelationships
        {
            Relations = relations,
            SourceStoryIds = sourceStories.Select(story => story.Id).ToArray(),
            CommonParentUrl = commonParentUrl,
        };
    }

    public void Dispose() => _http.Dispose();

    private async Task<IReadOnlyList<AzureDevOpsWorkItemRelationship>> ResolveSourceStoriesAsync(
        AzureDevOpsManagedRepository repository,
        string pullRequestId,
        string originatingWorkItemId,
        CancellationToken cancellationToken
    )
    {
        if (originatingWorkItemId.Length > 0)
        {
            var originating = await FetchWorkItemsAsync(
                repository,
                [originatingWorkItemId],
                cancellationToken
            );
            if (originating.TryGetValue(originatingWorkItemId, out var sourceStory))
            {
                return [sourceStory];
            }
        }

        var linkedIds = await FetchLinkedWorkItemIdsAsync(
            repository,
            pullRequestId,
            cancellationToken
        );
        if (linkedIds.Count == 0)
        {
            return [];
        }

        var linkedItems = await FetchWorkItemsAsync(
            repository,
            linkedIds,
            cancellationToken
        );
        return linkedIds
            .Select(id => linkedItems.GetValueOrDefault(id))
            .Where(item => item is not null
                && item.WorkItemType.Equals("User Story", StringComparison.OrdinalIgnoreCase))
            .Select(item => item!)
            .ToArray();
    }

    private async Task<AzureDevOpsPullRequestIdentity> FetchPullRequestIdentityAsync(
        AzureDevOpsManagedRepository repository,
        string pullRequestId,
        CancellationToken cancellationToken
    )
    {
        var endpoint = BuildPullRequestEndpoint(repository, pullRequestId);
        using var response = await _http.GetAsync(endpoint, cancellationToken);
        EnsureSuccess(response, "read pull request identity", repository, pullRequestId);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: cancellationToken
        );
        var root = document.RootElement;
        var repositoryId = ReadNestedIdentifier(root, "repository", "id");
        var projectId = ReadNestedIdentifier(root, "repository", "project", "id");
        if (repositoryId.Length == 0 || projectId.Length == 0)
        {
            throw new InvalidDataException(
                $"Azure DevOps returned incomplete artifact coordinates for pull request "
                    + $"'{pullRequestId}' in managed repository '{repository.RepositoryKey}'."
            );
        }

        return new AzureDevOpsPullRequestIdentity(projectId, repositoryId);
    }

    private async Task<IReadOnlyList<string>> FetchLinkedWorkItemIdsAsync(
        AzureDevOpsManagedRepository repository,
        string pullRequestId,
        CancellationToken cancellationToken
    )
    {
        var endpoint = BuildPullRequestEndpoint(repository, pullRequestId, "workitems");
        using var response = await _http.GetAsync(endpoint, cancellationToken);
        EnsureSuccess(response, "read linked work items", repository, pullRequestId);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: cancellationToken
        );
        if (!document.RootElement.TryGetProperty("value", out var values)
            || values.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException(
                $"Azure DevOps returned invalid linked work items for pull request "
                    + $"'{pullRequestId}' in managed repository '{repository.RepositoryKey}'."
            );
        }

        return OrderIdentifiers(
            values.EnumerateArray().Select(value => ReadIdentifier(value, "id"))
        );
    }

    private async Task<IReadOnlyDictionary<string, AzureDevOpsWorkItemRelationship>>
        FetchWorkItemsAsync(
            AzureDevOpsManagedRepository repository,
            IReadOnlyList<string> workItemIds,
            CancellationToken cancellationToken
        )
    {
        var results = new Dictionary<string, AzureDevOpsWorkItemRelationship>(
            StringComparer.OrdinalIgnoreCase
        );

        foreach (var batch in OrderIdentifiers(workItemIds).Chunk(MaximumWorkItemsPerRequest))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var endpoint = $"{Uri.EscapeDataString(repository.Project)}/_apis/wit/workitems"
                + $"?ids={Uri.EscapeDataString(string.Join(',', batch))}"
                + "&$expand=Relations&api-version=7.1";
            using var response = await _http.GetAsync(endpoint, cancellationToken);
            EnsureSuccess(response, "read work item relationships", repository, null);

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(
                stream,
                cancellationToken: cancellationToken
            );
            if (!document.RootElement.TryGetProperty("value", out var values)
                || values.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException(
                    $"Azure DevOps returned invalid work item relationships for managed "
                        + $"repository '{repository.RepositoryKey}'."
                );
            }

            foreach (var value in values.EnumerateArray())
            {
                var item = MapWorkItem(value);
                if (item is not null)
                {
                    results.TryAdd(item.Id, item);
                }
            }
        }

        return results;
    }

    private AzureDevOpsWorkItemRelationship? MapWorkItem(JsonElement element)
    {
        var id = ReadIdentifier(element, "id");
        if (id.Length == 0)
        {
            return null;
        }

        var url = ReadString(element, "url");
        if (!Uri.TryCreate(url, UriKind.Absolute, out _))
        {
            url = $"{_organizationUrl}/_apis/wit/workItems/{Uri.EscapeDataString(id)}";
        }

        var workItemType = string.Empty;
        if (element.TryGetProperty("fields", out var fields)
            && fields.ValueKind == JsonValueKind.Object)
        {
            workItemType = ReadString(fields, "System.WorkItemType");
        }

        var parentUrls = new List<string>();
        if (element.TryGetProperty("relations", out var relations)
            && relations.ValueKind == JsonValueKind.Array)
        {
            foreach (var relation in relations.EnumerateArray())
            {
                if (!ReadString(relation, "rel").Equals(
                    ParentRelationType,
                    StringComparison.OrdinalIgnoreCase
                ))
                {
                    continue;
                }

                var parentUrl = ReadString(relation, "url");
                if (Uri.TryCreate(parentUrl, UriKind.Absolute, out _))
                {
                    parentUrls.Add(parentUrl);
                }
            }
        }

        return new AzureDevOpsWorkItemRelationship(
            id,
            url,
            workItemType,
            parentUrls.Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
        );
    }

    private static string? FindCommonParentUrl(
        IReadOnlyList<AzureDevOpsWorkItemRelationship> sourceStories
    )
    {
        if (sourceStories.Count == 0
            || sourceStories.Any(story => story.ParentUrls.Count != 1))
        {
            return null;
        }

        var candidate = sourceStories[0].ParentUrls[0];
        return sourceStories.All(story => story.ParentUrls[0].Equals(
            candidate,
            StringComparison.OrdinalIgnoreCase
        ))
            ? candidate
            : null;
    }

    private static string BuildPullRequestArtifactUri(
        string projectId,
        string repositoryId,
        string pullRequestId
    )
    {
        var artifactId = string.Join('/', projectId, repositoryId, pullRequestId.Trim());
        return $"vstfs:///Git/PullRequestId/{Uri.EscapeDataString(artifactId)}";
    }

    private static string BuildPullRequestEndpoint(
        AzureDevOpsManagedRepository repository,
        string pullRequestId,
        string? suffix = null
    )
    {
        var endpoint = $"{Uri.EscapeDataString(repository.Project)}/_apis/git/repositories/"
            + $"{Uri.EscapeDataString(repository.RemoteIdentity)}/pullRequests/"
            + Uri.EscapeDataString(pullRequestId.Trim());
        if (!string.IsNullOrWhiteSpace(suffix))
        {
            endpoint += $"/{Uri.EscapeDataString(suffix)}";
        }

        return endpoint + "?api-version=7.1";
    }

    private static void EnsureSuccess(
        HttpResponseMessage response,
        string operation,
        AzureDevOpsManagedRepository repository,
        string? pullRequestId
    )
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var target = pullRequestId is null
            ? $"managed repository '{repository.RepositoryKey}'"
            : $"pull request '{pullRequestId}' in managed repository '{repository.RepositoryKey}'";
        throw new HttpRequestException(
            $"Azure DevOps failed to {operation} for {target}: HTTP {(int)response.StatusCode}.",
            inner: null,
            statusCode: response.StatusCode
        );
    }

    private static string[] OrderIdentifiers(IEnumerable<string> identifiers) =>
        identifiers
            .Select(Clean)
            .Where(identifier => identifier.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(identifier => long.TryParse(
                identifier,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var numericId
            ) ? numericId : long.MaxValue)
            .ThenBy(identifier => identifier, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string ReadNestedIdentifier(
        JsonElement element,
        params string[] path
    )
    {
        var current = element;
        foreach (var propertyName in path)
        {
            if (current.ValueKind != JsonValueKind.Object
                || !current.TryGetProperty(propertyName, out current))
            {
                return string.Empty;
            }
        }

        return ReadIdentifier(current);
    }

    private static string ReadIdentifier(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property)
            ? ReadIdentifier(property)
            : string.Empty;

    private static string ReadIdentifier(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => Clean(element.GetString()),
        JsonValueKind.Number when element.TryGetInt64(out var number) =>
            number.ToString(CultureInfo.InvariantCulture),
        _ => string.Empty,
    };

    private static string ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property)
        && property.ValueKind == JsonValueKind.String
            ? Clean(property.GetString())
            : string.Empty;

    private static string Clean(string? value) => value?.Trim() ?? string.Empty;

    private sealed record AzureDevOpsPullRequestIdentity(
        string ProjectId,
        string RepositoryId
    );

    private sealed record AzureDevOpsWorkItemRelationship(
        string Id,
        string Url,
        string WorkItemType,
        IReadOnlyList<string> ParentUrls
    );
}

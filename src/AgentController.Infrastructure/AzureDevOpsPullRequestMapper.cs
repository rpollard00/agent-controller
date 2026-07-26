using System.Globalization;
using System.Text.Json;
using AgentController.Application;
using AgentController.Domain;

namespace AgentController.Infrastructure;

/// <summary>
/// Managed Azure DevOps repository coordinates used while discovering pull requests.
/// Credentials intentionally live outside this value.
/// </summary>
internal sealed record AzureDevOpsManagedRepository
{
    public string EnvironmentKey { get; init; } = string.Empty;

    public string RepositoryKey { get; init; } = string.Empty;

    public string Project { get; init; } = string.Empty;

    public string RemoteIdentity { get; init; } = string.Empty;

    public string? RepositoryWebUrl { get; init; }
}

/// <summary>A mapped pull request plus collection-presence flags used for lazy hydration.</summary>
internal sealed record AzureDevOpsPullRequestItem
{
    public ManagedPullRequestSnapshot Snapshot { get; init; } = new();

    public bool HasLabels { get; init; }

    public bool HasLinkedWorkItems { get; init; }
}

/// <summary>Pure Azure DevOps pull-request JSON mapping helpers.</summary>
internal static class AzureDevOpsPullRequestMapper
{
    public static AzureDevOpsPullRequestItem? MapPullRequest(
        JsonElement element,
        AzureDevOpsManagedRepository repository,
        string organizationUrl
    )
    {
        var status = ReadString(element, "status");
        if (status.Length > 0
            && !status.Equals("active", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var pullRequestId = ReadIdentifier(element, "pullRequestId");
        if (pullRequestId.Length == 0)
        {
            return null;
        }

        var labels = ReadLabels(element, out var hasLabels);
        var linkedWorkItems = ReadWorkItems(element, "workItemRefs", out var hasLinkedWorkItems);
        var remoteRepositoryName = ReadNestedString(element, "repository", "name");
        var remoteRepositoryWebUrl = ReadNestedString(element, "repository", "webUrl");

        return new AzureDevOpsPullRequestItem
        {
            Snapshot = new ManagedPullRequestSnapshot
            {
                PullRequest = new PullRequestReference
                {
                    EnvironmentKey = repository.EnvironmentKey,
                    RepositoryKey = repository.RepositoryKey,
                    PullRequestId = pullRequestId,
                    PullRequestUrl = ResolvePullRequestUrl(
                        element,
                        repository,
                        organizationUrl,
                        remoteRepositoryName,
                        remoteRepositoryWebUrl,
                        pullRequestId
                    ),
                    SourceBranch = StripBranchPrefix(ReadString(element, "sourceRefName")),
                    TargetBranch = StripBranchPrefix(ReadString(element, "targetRefName")),
                    SourceCommitSha = ReadNestedString(
                        element,
                        "lastMergeSourceCommit",
                        "commitId"
                    ),
                },
                Title = ReadString(element, "title"),
                Labels = labels,
                LinkedWorkItems = linkedWorkItems,
            },
            HasLabels = hasLabels,
            HasLinkedWorkItems = hasLinkedWorkItems,
        };
    }

    public static IReadOnlyList<string> MapLabelsResponse(JsonElement root)
    {
        if (!TryGetArray(root, "value", out var values))
        {
            return [];
        }

        return values
            .EnumerateArray()
            .Select(label => ReadString(label, "name"))
            .Where(label => label.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static IReadOnlyList<PullRequestWorkItemReference> MapWorkItemsResponse(
        JsonElement root
    )
    {
        if (!TryGetArray(root, "value", out var values))
        {
            return [];
        }

        return MapWorkItems(values);
    }

    private static string[] ReadLabels(
        JsonElement element,
        out bool propertyWasPresent
    )
    {
        propertyWasPresent = element.TryGetProperty("labels", out var labels)
            && labels.ValueKind == JsonValueKind.Array;
        if (!propertyWasPresent)
        {
            return [];
        }

        return labels
            .EnumerateArray()
            .Select(label => ReadString(label, "name"))
            .Where(label => label.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static PullRequestWorkItemReference[] ReadWorkItems(
        JsonElement element,
        string propertyName,
        out bool propertyWasPresent
    )
    {
        propertyWasPresent = element.TryGetProperty(propertyName, out var workItems)
            && workItems.ValueKind == JsonValueKind.Array;
        if (!propertyWasPresent)
        {
            return [];
        }

        return MapWorkItems(workItems);
    }

    private static PullRequestWorkItemReference[] MapWorkItems(
        JsonElement values
    )
    {
        return values
            .EnumerateArray()
            .Select(workItem => new PullRequestWorkItemReference
            {
                WorkItemId = ReadIdentifier(workItem, "id"),
                WorkItemUrl = ReadString(workItem, "url"),
            })
            .Where(workItem => workItem.WorkItemId.Length > 0)
            .GroupBy(workItem => workItem.WorkItemId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
    }

    private static string ResolvePullRequestUrl(
        JsonElement element,
        AzureDevOpsManagedRepository repository,
        string organizationUrl,
        string remoteRepositoryName,
        string remoteRepositoryWebUrl,
        string pullRequestId
    )
    {
        var linkedUrl = ReadNestedString(element, "_links", "web", "href");
        if (linkedUrl.Length > 0)
        {
            return linkedUrl;
        }

        var repositoryWebUrl = FirstNonEmpty(
            remoteRepositoryWebUrl,
            repository.RepositoryWebUrl
        );
        if (repositoryWebUrl.Length > 0)
        {
            return $"{repositoryWebUrl.TrimEnd('/')}/pullrequest/{Uri.EscapeDataString(pullRequestId)}";
        }

        var repositoryPath = FirstNonEmpty(remoteRepositoryName, repository.RemoteIdentity);
        return $"{organizationUrl.TrimEnd('/')}/{Uri.EscapeDataString(repository.Project)}"
            + $"/_git/{Uri.EscapeDataString(repositoryPath)}"
            + $"/pullrequest/{Uri.EscapeDataString(pullRequestId)}";
    }

    private static string ReadNestedString(
        JsonElement element,
        params string[] propertyPath
    )
    {
        var current = element;
        foreach (var propertyName in propertyPath)
        {
            if (current.ValueKind != JsonValueKind.Object
                || !current.TryGetProperty(propertyName, out current))
            {
                return string.Empty;
            }
        }

        return current.ValueKind == JsonValueKind.String
            ? current.GetString()?.Trim() ?? string.Empty
            : string.Empty;
    }

    private static string ReadString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String
                ? property.GetString()?.Trim() ?? string.Empty
                : string.Empty;
    }

    private static string ReadIdentifier(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return string.Empty;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString()?.Trim() ?? string.Empty,
            JsonValueKind.Number => property.TryGetInt64(out var number)
                ? number.ToString(CultureInfo.InvariantCulture)
                : property.GetRawText(),
            _ => string.Empty,
        };
    }

    private static bool TryGetArray(
        JsonElement element,
        string propertyName,
        out JsonElement values
    )
    {
        return element.TryGetProperty(propertyName, out values)
            && values.ValueKind == JsonValueKind.Array;
    }

    private static string StripBranchPrefix(string branch)
    {
        const string prefix = "refs/heads/";
        return branch.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? branch[prefix.Length..]
            : branch;
    }

    private static string FirstNonEmpty(string? first, string? second)
    {
        if (!string.IsNullOrWhiteSpace(first))
        {
            return first.Trim();
        }

        return second?.Trim() ?? string.Empty;
    }
}

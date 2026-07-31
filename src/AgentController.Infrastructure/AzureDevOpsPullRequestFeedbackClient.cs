using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AgentController.Application;
using AgentController.Domain;

namespace AgentController.Infrastructure;

/// <summary>Creates authenticated Azure DevOps pull-request feedback clients.</summary>
internal abstract class AzureDevOpsPullRequestFeedbackClientFactory
{
    public abstract AzureDevOpsPullRequestFeedbackClient Create(
        string organizationUrl,
        string personalAccessToken
    );
}

/// <summary>Default HTTP-client factory for Azure DevOps pull-request feedback.</summary>
internal sealed class DefaultAzureDevOpsPullRequestFeedbackClientFactory
    : AzureDevOpsPullRequestFeedbackClientFactory
{
    public override AzureDevOpsPullRequestFeedbackClient Create(
        string organizationUrl,
        string personalAccessToken
    ) => new(new HttpClient(), organizationUrl, personalAccessToken);
}

/// <summary>
/// Narrow Azure DevOps Repos REST client for reading pull-request review threads.
/// Repository coordinates are supplied by the managed target rather than derived from
/// the pull-request display URL.
/// </summary>
internal sealed class AzureDevOpsPullRequestFeedbackClient : IDisposable
{
    private static readonly Dictionary<string, ReviewThreadStatus> StatusMap = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        ["active"] = ReviewThreadStatus.Active,
        ["resolved"] = ReviewThreadStatus.Resolved,
        ["fixed"] = ReviewThreadStatus.Fixed,
        ["wontfix"] = ReviewThreadStatus.WontFix,
        ["closed"] = ReviewThreadStatus.Closed,
        ["bydesign"] = ReviewThreadStatus.ByDesign,
    };

    private readonly HttpClient _http;

    public AzureDevOpsPullRequestFeedbackClient(
        HttpClient http,
        string organizationUrl,
        string personalAccessToken
    )
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentException.ThrowIfNullOrWhiteSpace(organizationUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(personalAccessToken);

        _http = http;
        _http.BaseAddress = new Uri(organizationUrl.TrimEnd('/') + "/", UriKind.Absolute);
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

    /// <summary>Reads all review threads for one managed pull request.</summary>
    public async Task<IReadOnlyList<ReviewThread>> GetThreadsAsync(
        AzureDevOpsManagedRepository repository,
        string pullRequestId,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(pullRequestId);
        cancellationToken.ThrowIfCancellationRequested();

        var endpoint = BuildThreadsEndpoint(repository, pullRequestId);
        using var response = await _http.GetAsync(endpoint, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return [];
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: cancellationToken
        );
        if (!document.RootElement.TryGetProperty("value", out var values)
            || values.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var threads = new List<ReviewThread>();
        foreach (var thread in values.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            threads.Add(MapThread(thread));
        }

        return threads;
    }

    public void Dispose() => _http.Dispose();

    private static ReviewThread MapThread(JsonElement thread)
    {
        var threadId = ReadIdentifier(thread, "id");
        var status = ReviewThreadStatus.Active;
        var statusValue = ReadString(thread, "status");
        if (StatusMap.TryGetValue(statusValue, out var mappedStatus))
        {
            status = mappedStatus;
        }

        var (filePath, startLine, endLine, isFileLevel) = ParseThreadContext(thread);
        var comments = new List<ReviewThreadComment>();
        if (thread.TryGetProperty("comments", out var commentValues)
            && commentValues.ValueKind == JsonValueKind.Array)
        {
            foreach (var comment in commentValues.EnumerateArray())
            {
                comments.Add(MapComment(comment));
            }
        }

        return new ReviewThread
        {
            ThreadId = threadId,
            Author = comments.Count > 0 ? comments[0].Author : string.Empty,
            CreatedAt = ParseDateTimeOffset(thread, "publishedDate")
                ?? DateTimeOffset.UtcNow,
            Status = status,
            FilePath = filePath,
            StartLine = startLine,
            EndLine = endLine,
            IsFileLevel = isFileLevel,
            Comments = comments,
        };
    }

    private static ReviewThreadComment MapComment(JsonElement comment)
    {
        var author = string.Empty;
        var authorIdentities = new List<ReviewerIdentity>();
        if (comment.TryGetProperty("author", out var authorElement)
            && authorElement.ValueKind == JsonValueKind.Object)
        {
            var displayName = ReadString(authorElement, "displayName");
            var uniqueName = ReadString(authorElement, "uniqueName");
            var identityId = ReadString(authorElement, "id");
            var descriptor = ReadString(authorElement, "descriptor");

            author = FirstNonEmpty(displayName, uniqueName, identityId, descriptor);
            AddAuthorIdentity(
                authorIdentities,
                AzureDevOpsReviewerIdentityKinds.Email,
                uniqueName
            );
            AddAuthorIdentity(
                authorIdentities,
                AzureDevOpsReviewerIdentityKinds.IdentityId,
                identityId
            );
            AddAuthorIdentity(
                authorIdentities,
                AzureDevOpsReviewerIdentityKinds.Descriptor,
                descriptor
            );
        }

        return new ReviewThreadComment
        {
            Author = author,
            AuthorIdentities = authorIdentities,
            Body = ReadString(comment, "content"),
            CreatedAt = ParseDateTimeOffset(comment, "publishedDate")
                ?? DateTimeOffset.UtcNow,
            IsReply = comment.TryGetProperty("parentCommentId", out var parentCommentId)
                && parentCommentId.ValueKind != JsonValueKind.Null,
        };
    }

    private static void AddAuthorIdentity(
        List<ReviewerIdentity> identities,
        string kind,
        string value
    )
    {
        if (value.Length > 0)
        {
            identities.Add(new ReviewerIdentity { Kind = kind, Value = value });
        }
    }

    private static (string? FilePath, int? StartLine, int? EndLine, bool IsFileLevel)
        ParseThreadContext(JsonElement thread)
    {
        if (!thread.TryGetProperty("threadContext", out var context)
            || context.ValueKind != JsonValueKind.Object)
        {
            return (null, null, null, false);
        }

        string? filePath = null;
        int? startLine = null;
        int? endLine = null;
        var isFileLevel = false;

        var filePathValue = ReadString(context, "filePath");
        if (filePathValue.Length > 0)
        {
            filePath = filePathValue;
        }

        if (context.TryGetProperty("rightFileStart", out var rightStart)
            && rightStart.ValueKind == JsonValueKind.Object
            && rightStart.TryGetProperty("line", out var start)
            && start.ValueKind == JsonValueKind.Number
            && start.TryGetInt32(out var parsedStart))
        {
            startLine = parsedStart;
        }

        if (context.TryGetProperty("rightFileEnd", out var rightEnd)
            && rightEnd.ValueKind == JsonValueKind.Object
            && rightEnd.TryGetProperty("line", out var end)
            && end.ValueKind == JsonValueKind.Number
            && end.TryGetInt32(out var parsedEnd))
        {
            endLine = parsedEnd;
        }

        if (context.TryGetProperty("isFileLevel", out var fileLevel)
            && (fileLevel.ValueKind == JsonValueKind.True
                || fileLevel.ValueKind == JsonValueKind.False))
        {
            isFileLevel = fileLevel.GetBoolean();
        }

        return (filePath, startLine, endLine, isFileLevel);
    }

    private static DateTimeOffset? ParseDateTimeOffset(
        JsonElement element,
        string propertyName
    )
    {
        var value = ReadString(element, propertyName);
        return DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;
    }

    private static string BuildThreadsEndpoint(
        AzureDevOpsManagedRepository repository,
        string pullRequestId
    ) => $"{Uri.EscapeDataString(repository.Project)}/_apis/git/repositories/"
        + $"{Uri.EscapeDataString(repository.RemoteIdentity)}/pullRequests/"
        + $"{Uri.EscapeDataString(pullRequestId.Trim())}/threads?api-version=7.1";

    private static string ReadString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()?.Trim() ?? string.Empty
                : string.Empty;
    }

    private static string ReadIdentifier(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return string.Empty;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString()?.Trim() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            _ => string.Empty,
        };
    }

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(value => value.Length > 0) ?? string.Empty;
}

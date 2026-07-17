using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AgentController.Application;
using AgentController.Domain;
using AgentController.Domain.Secrets;

namespace AgentController.Infrastructure;

/// <summary>
/// <see cref="IFeedbackSource"/> implementation against the Azure DevOps Repos REST API.
///
/// Calls <c>GET {project}/_apis/git/repositories/{repository}/pullRequests/{pullRequestId}/threads?api-version=7.1</c>
/// for each PR in the query and maps the ADO thread model into
/// <see cref="ReviewThread"/> domain records.
///
/// PAT resolution: for each PR, looks up the repository profile via
/// <see cref="IRepositoryStore"/>, resolves the owning unified connection
/// via <see cref="IConnectionStore"/>, then obtains the PAT
/// from the connection's named secret reference through <see cref="ISecretStore"/>.
///
/// This source is a pure fetcher — it returns raw threads without any filtering.
/// All filtering (marker gate, allowlist, status, author, content) is the
/// responsibility of the upstream worker filter pipeline.
/// </summary>
internal sealed class AzureDevOpsReposFeedbackSource : IFeedbackSource
{
    private readonly IRepositoryStore _repositoryStore;
    private readonly IConnectionStore _connectionStore;
    private readonly ISecretStore _secretStore;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// Mapping from Azure DevOps thread status string values to
    /// <see cref="ReviewThreadStatus"/> enum members.
    /// </summary>
    private static readonly Dictionary<string, ReviewThreadStatus> StatusMap = new(StringComparer.OrdinalIgnoreCase)
    {
        { "active", ReviewThreadStatus.Active },
        { "resolved", ReviewThreadStatus.Resolved },
        { "fixed", ReviewThreadStatus.Fixed },
        { "wontfix", ReviewThreadStatus.WontFix },
        { "closed", ReviewThreadStatus.Closed },
        { "bydesign", ReviewThreadStatus.ByDesign },
    };

    public AzureDevOpsReposFeedbackSource(
        IRepositoryStore repositoryStore,
        IConnectionStore connectionStore,
        ISecretStore secretStore)
    {
        _repositoryStore = repositoryStore;
        _connectionStore = connectionStore;
        _secretStore = secretStore;
    }

    public async Task<IReadOnlyList<ReworkSignal>> PollAsync(
        FeedbackQuery query,
        CancellationToken cancellationToken)
    {
        if (query.OpenPrs.Count == 0)
        {
            return [];
        }

        var signals = new List<ReworkSignal>();

        foreach (var pr in query.OpenPrs)
        {
            // Parse project, repository and derive base URL from the PR URL.
            var parsed = ParsePullRequestUrlWithOrg(pr.PullRequestUrl);
            if (parsed is null)
            {
                // Cannot determine project/repo from URL — skip this PR.
                continue;
            }

            var (organizationUrl, project, repository) = parsed.Value;

            // Resolve PAT from the repo-host connection profile that owns this repo.
            var pat = await ResolvePatForRepoAsync(pr.RepoKey, cancellationToken);
            if (string.IsNullOrWhiteSpace(pat))
            {
                // No PAT available for this repo — skip.
                continue;
            }

            // Build per-PR HTTP client with the resolved PAT.
            using var http = CreateAuthenticatedClient(organizationUrl, pat);

            // Build the threads API endpoint.
            var endpoint = $"{project}/_apis/git/repositories/{Uri.EscapeDataString(repository)}/pullRequests/{Uri.EscapeDataString(pr.PullRequestId)}/threads?api-version=7.1";

            var threads = await FetchThreadsAsync(http, endpoint, cancellationToken);

            if (threads.Count == 0)
            {
                continue;
            }

            // Compute qualifying comment timestamps across all threads.
            var firstQualifyingCommentAt = threads
                .SelectMany(t => t.Comments)
                .Min(c => c.CreatedAt);

            var lastQualifyingCommentAt = threads
                .SelectMany(t => t.Comments)
                .Max(c => c.CreatedAt);

            signals.Add(new ReworkSignal
            {
                OriginatingRunId = pr.OriginatingRunId,
                PullRequestId = pr.PullRequestId,
                Threads = threads,
                FirstQualifyingCommentAt = firstQualifyingCommentAt,
                LastQualifyingCommentAt = lastQualifyingCommentAt,
            });
        }

        return signals;
    }

    /// <summary>
    /// Resolve the PAT for a repository by looking up its owning unified connection
    /// profile and resolving the connection's named secret reference through ISecretStore.
    /// </summary>
    private async Task<string?> ResolvePatForRepoAsync(
        string repoKey,
        CancellationToken cancellationToken)
    {
        // Step 1: Look up the repository profile.
        var repository = await _repositoryStore.GetByKeyAsync(repoKey, cancellationToken);
        if (repository is null || string.IsNullOrWhiteSpace(repository.RepositoryHostConnectionKey))
        {
            return null;
        }

        // Step 2: Look up the unified connection profile.
        var connection = await _connectionStore.GetByKeyAsync(
            repository.RepositoryHostConnectionKey,
            cancellationToken);

        if (connection is null || !connection.Enabled)
        {
            return null;
        }

        // Step 3: Extract ADO settings and resolve the PAT from the connection's secret reference.
        var settings = connection.ProviderSettings as AzureDevOpsConnectionSettings;
        if (settings is null)
        {
            return null;
        }

        var secretName = settings.PersonalAccessTokenReference.Name;
        if (string.IsNullOrWhiteSpace(secretName))
        {
            return null;
        }

        return await _secretStore.ResolveAsync(secretName.Trim(), cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Create an HTTP client configured with the Azure DevOps base URL and PAT auth.
    /// </summary>
    private static HttpClient CreateAuthenticatedClient(string organizationUrl, string pat)
    {
        var http = new HttpClient();
        http.BaseAddress = new Uri(organizationUrl.TrimEnd('/') + "/");

        var authBytes = Encoding.ASCII.GetBytes($":{pat}");
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Basic", Convert.ToBase64String(authBytes));

        return http;
    }

    /// <summary>
    /// Fetch and parse threads from the ADO Threads API endpoint.
    /// Returns an empty list on HTTP failure rather than throwing.
    /// </summary>
    private static async Task<IReadOnlyList<ReviewThread>> FetchThreadsAsync(
        HttpClient http,
        string endpoint,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await http.GetAsync(endpoint, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                // Non-success: return empty list (caller skips PRs with no threads).
                return [];
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);

            var threads = new List<ReviewThread>();

            if (!doc.RootElement.TryGetProperty("value", out var valueArray)
                || valueArray.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            foreach (var thread in valueArray.EnumerateArray())
            {
                // Thread ID (ADO uses integer IDs, map to string).
                var threadId = thread.TryGetProperty("id", out var idEl)
                               && idEl.ValueKind == JsonValueKind.Number
                    ? idEl.GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : string.Empty;

                // Thread status — map from ADO string to our enum.
                var status = ReviewThreadStatus.Active;
                if (thread.TryGetProperty("status", out var statusEl)
                    && statusEl.ValueKind == JsonValueKind.String)
                {
                    var statusStr = statusEl.GetString();
                    if (!string.IsNullOrWhiteSpace(statusStr)
                        && StatusMap.TryGetValue(statusStr, out var mappedStatus))
                    {
                        status = mappedStatus;
                    }
                }

                // Thread creation date.
                var createdAt = ParseDateTimeOffset(thread, "publishedDate") ?? DateTimeOffset.UtcNow;

                // Thread context (file path, line range, isFileLevel).
                var (filePath, startLine, endLine, isFileLevel) = ParseThreadContext(thread);

                // Parse comments in the thread.
                var comments = new List<ReviewThreadComment>();
                if (thread.TryGetProperty("comments", out var commentsArray)
                    && commentsArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (var comment in commentsArray.EnumerateArray())
                    {
                        // Author: use uniqueName (email) as canonical identifier.
                        var author = string.Empty;
                        if (comment.TryGetProperty("author", out var authorEl)
                            && authorEl.ValueKind == JsonValueKind.Object
                            && authorEl.TryGetProperty("uniqueName", out var uniqueNameEl)
                            && uniqueNameEl.ValueKind == JsonValueKind.String)
                        {
                            author = uniqueNameEl.GetString() ?? string.Empty;
                        }

                        // Comment body.
                        var body = comment.TryGetProperty("content", out var contentEl)
                                   && contentEl.ValueKind == JsonValueKind.String
                            ? contentEl.GetString() ?? string.Empty
                            : string.Empty;

                        // Comment timestamp.
                        var commentCreatedAt = ParseDateTimeOffset(comment, "publishedDate")
                            ?? DateTimeOffset.UtcNow;

                        // IsReply: true if parentCommentId is present and non-null.
                        var isReply = comment.TryGetProperty("parentCommentId", out var parentEl)
                                      && parentEl.ValueKind != JsonValueKind.Null;

                        comments.Add(new ReviewThreadComment
                        {
                            Author = author,
                            Body = body,
                            CreatedAt = commentCreatedAt,
                            IsReply = isReply,
                        });
                    }
                }

                // Canonical thread author: first comment author (thread starter).
                var threadAuthor = comments.Count > 0 ? comments[0].Author : string.Empty;

                threads.Add(new ReviewThread
                {
                    ThreadId = threadId,
                    Author = threadAuthor,
                    CreatedAt = createdAt,
                    Status = status,
                    FilePath = filePath,
                    StartLine = startLine,
                    EndLine = endLine,
                    IsFileLevel = isFileLevel,
                    Comments = comments,
                });
            }

            return threads;
        }
        catch (OperationCanceledException)
        {
            // Propagate cancellation.
            throw;
        }
        catch
        {
            // Any other error (network, JSON parsing) — return empty list.
            // The caller skips PRs with no threads; this is a pure fetcher.
            return [];
        }
    }

    /// <summary>
    /// Parse the <c>threadContext</c> object from an ADO thread to extract
    /// file path, line range, and whether the thread is file-level.
    /// </summary>
    private static (string? FilePath, int? StartLine, int? EndLine, bool IsFileLevel) ParseThreadContext(
        JsonElement thread)
    {
        string? filePath = null;
        int? startLine = null;
        int? endLine = null;
        bool isFileLevel = false;

        if (thread.TryGetProperty("threadContext", out var context)
            && context.ValueKind == JsonValueKind.Object)
        {
            // File path.
            if (context.TryGetProperty("filePath", out var filePathEl)
                && filePathEl.ValueKind == JsonValueKind.String)
            {
                filePath = filePathEl.GetString();
            }

            // Line range (1-based, inclusive).
            if (context.TryGetProperty("rightFileStart", out var rightStart)
                && rightStart.ValueKind == JsonValueKind.Object
                && rightStart.TryGetProperty("line", out var startLineEl)
                && startLineEl.ValueKind == JsonValueKind.Number)
            {
                startLine = startLineEl.GetInt32();
            }

            if (context.TryGetProperty("rightFileEnd", out var rightEnd)
                && rightEnd.ValueKind == JsonValueKind.Object
                && rightEnd.TryGetProperty("line", out var endLineEl)
                && endLineEl.ValueKind == JsonValueKind.Number)
            {
                endLine = endLineEl.GetInt32();
            }

            // isFileLevel: true when the thread is a PR-level comment (not on a specific line).
            if (context.TryGetProperty("isFileLevel", out var isFileLevelEl)
                && (isFileLevelEl.ValueKind == JsonValueKind.True || isFileLevelEl.ValueKind == JsonValueKind.False))
            {
                isFileLevel = isFileLevelEl.GetBoolean();
            }
        }

        return (filePath, startLine, endLine, isFileLevel);
    }

    /// <summary>
    /// Parse an ISO 8601 date-time string from a JSON element property.
    /// Returns <c>null</c> when the property is missing or unparseable.
    /// </summary>
    private static DateTimeOffset? ParseDateTimeOffset(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var dateEl)
            && dateEl.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(dateEl.GetString(), out var parsed))
        {
            return parsed;
        }

        return null;
    }

    /// <summary>
    /// Parse an Azure DevOps pull request URL to extract organization base URL,
    /// project and repository name.
    ///
    /// Supported URL formats:
    /// <list type="bullet">
    ///   <item><description>
    ///     <c>https://dev.azure.com/{organization}/{project}/_git/{repository}/pullrequest/{id}</c>
    ///   </description></item>
    ///   <item><description>
    ///     <c>https://{organization}.visualstudio.com/{project}/_git/{repository}/pullrequest/{id}</c>
    ///   </description></item>
    /// </list>
    ///
    /// Returns <c>null</c> when the URL cannot be parsed.
    /// </summary>
    private static (string OrganizationUrl, string Project, string Repository)? ParsePullRequestUrlWithOrg(
        string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var segments = uri.Segments;

        // Find the "_git/" segment to anchor the parse.
        for (int i = 0; i < segments.Length; i++)
        {
            if (!segments[i].Equals("_git/", StringComparison.Ordinal))
            {
                continue;
            }

            if (i <= 0 || i + 1 >= segments.Length)
            {
                break;
            }

            var project = segments[i - 1].TrimEnd('/');
            var repository = segments[i + 1].TrimEnd('/');

            if (string.IsNullOrWhiteSpace(project) || string.IsNullOrWhiteSpace(repository))
            {
                break;
            }

            // Derive the organization base URL from the PR URL.
            var organizationUrl = DeriveOrganizationUrl(uri);
            if (organizationUrl is null)
            {
                break;
            }

            return (organizationUrl, project, repository);
        }

        return null;
    }

    /// <summary>
    /// Derive the Azure DevOps organization base URL from a PR URI.
    /// </summary>
    private static string? DeriveOrganizationUrl(Uri uri)
    {
        if (uri.Host.Equals("dev.azure.com", StringComparison.Ordinal))
        {
            // https://dev.azure.com/{organization}/...
            var segments = uri.Segments;
            if (segments.Length > 0)
            {
                var organization = segments[0].TrimEnd('/');
                if (!string.IsNullOrWhiteSpace(organization))
                {
                    return $"{uri.Scheme}://{uri.Host}/{organization}";
                }
            }
        }
        else if (uri.Host.EndsWith(".visualstudio.com", StringComparison.Ordinal))
        {
            // https://{organization}.visualstudio.com/...
            return $"{uri.Scheme}://{uri.Host}";
        }

        return null;
    }
}

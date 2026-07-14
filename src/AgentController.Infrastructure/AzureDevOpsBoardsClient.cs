using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentController.Application;
using AgentController.Domain;
using AgentController.Infrastructure.Options;
using Microsoft.Extensions.Logging;

namespace AgentController.Infrastructure;

/// <summary>
/// <see cref="HttpClient"/>-based implementation of <see cref="IAzureDevOpsBoardsClient"/>
/// for Azure DevOps Boards REST APIs.
///
/// Authentication uses a Personal Access Token (PAT) resolved from
/// <see cref="AzureDevOpsBoardsOptions"/>. The client is registered as a transient
/// or scoped service via <c>AddAgentControllerAzureDevOpsBoardsWorkSource</c>.
///
/// API version used: 7.1 (Azure DevOps Services).
/// </summary>
internal sealed partial class AzureDevOpsBoardsClient : IAzureDevOpsBoardsClient, IDisposable
{
    private readonly HttpClient _http;
    private readonly AzureDevOpsBoardsOptions _options;
    private readonly ILogger<AzureDevOpsBoardsClient> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly string[] WorkItemFields =
    [
        "System.Id",
        "System.Title",
        "System.Description",
        "System.State",
        "System.Tags",
        "System.AssignedTo",
        "Microsoft.VSTS.Common.Priority",
        "Microsoft.VSTS.Common.AcceptanceCriteria",
        "System.AreaPath",
        "System.IterationPath",
        "System.WorkItemType",
    ];

    public AzureDevOpsBoardsClient(
        HttpClient http,
        AzureDevOpsBoardsOptions options,
        ILogger<AzureDevOpsBoardsClient> logger
    )
    {
        _http = http;
        _options = options;
        _logger = logger;

        // Configure the base address from options
        if (!string.IsNullOrWhiteSpace(options.BaseUrl))
        {
            _http.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
        }

        // Set Basic auth header with PAT
        var pat = options.ResolvePersonalAccessToken();
        if (!string.IsNullOrWhiteSpace(pat))
        {
            var authBytes = Encoding.ASCII.GetBytes($":{pat}");
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Basic",
                Convert.ToBase64String(authBytes)
            );
        }

        // Ensure JSON responses for all ADO REST API calls.
        // Without an explicit Accept header, some ADO endpoints may return
        // non-JSON or omit fields depending on field selection rules.
        _http.DefaultRequestHeaders.Accept.Clear();
        _http.DefaultRequestHeaders.Accept.Add(
            new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json")
        );
    }

    public async Task<IReadOnlyList<WorkCandidate>> QueryWorkItemsAsync(
        BoardsQueryParameters parameters,
        CancellationToken cancellationToken
    )
    {
        var project = string.IsNullOrWhiteSpace(parameters.Project)
            ? _options.Project
            : parameters.Project;

        if (string.IsNullOrWhiteSpace(project))
        {
            throw new InvalidOperationException(
                "Azure DevOps project name is required for work item queries. "
                    + "Configure 'workSource:project' or supply it in the query parameters."
            );
        }

        // Build WIQL query
        var wiql = BuildWiql(project, parameters);
        var wiqlBody = JsonSerializer.Serialize(new { query = wiql }, JsonOptions);
        var content = new StringContent(wiqlBody, Encoding.UTF8, "application/json");

        // POST WIQL query
        var wiqlResponse = await _http.PostAsync(
            $"{project}/_apis/wit/wiql?api-version=7.1",
            content,
            cancellationToken
        );

        wiqlResponse.EnsureSuccessStatusCode();

        var wiqlJson = await wiqlResponse.Content.ReadAsStringAsync(cancellationToken);
        using var wiqlDoc = JsonDocument.Parse(wiqlJson);
        var workItemRefs = wiqlDoc.RootElement.GetProperty("workItems").EnumerateArray().ToList();

        if (workItemRefs.Count == 0)
            return [];

        // Batch fetch work item details
        var ids = workItemRefs.Select(r => r.GetProperty("id").GetInt32()).ToList();

        var batchBody = JsonSerializer.Serialize(new { ids, fields = WorkItemFields }, JsonOptions);

        var batchContent = new StringContent(batchBody, Encoding.UTF8, "application/json");
        var batchResponse = await _http.PostAsync(
            $"{project}/_apis/wit/workitemsbatch?api-version=7.1",
            batchContent,
            cancellationToken
        );

        batchResponse.EnsureSuccessStatusCode();

        var batchJson = await batchResponse.Content.ReadAsStringAsync(cancellationToken);
        using var batchDoc = JsonDocument.Parse(batchJson);
        var workItems = batchDoc.RootElement.GetProperty("value").EnumerateArray();

        var results = new List<WorkCandidate>();
        foreach (var item in workItems)
        {
            var itemId = item.GetProperty("id").GetInt32();

            // Extract revision for optimistic concurrency on later updates
            var revision =
                item.TryGetProperty("rev", out var revEl)
                && revEl.ValueKind == JsonValueKind.Number
                && revEl.TryGetInt32(out var rev)
                    ? rev.ToString(CultureInfo.InvariantCulture)
                    : null;

            // Require the fields object for mapping
            if (
                !item.TryGetProperty("fields", out var fields)
                || fields.ValueKind != JsonValueKind.Object
            )
            {
                // Malformed: work item returned without fields — skip with warning
                continue;
            }

            // Parse tags
            var tags =
                fields.TryGetProperty("System.Tags", out var tagsElement)
                && tagsElement.ValueKind == JsonValueKind.String
                    ? tagsElement
                        .GetString()
                        ?.Split(
                            ';',
                            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
                        )
                        ?? Array.Empty<string>()
                    : Array.Empty<string>();

            // Resolve RepoKey from tags
            var repoKey = ResolveRepoKeyFromTags(tags);

            // Build SourceMetadata for later updates
            var metadata = new Dictionary<string, string>();
            if (revision is not null)
                metadata["revision"] = revision;

            var areaPath = GetStringField(fields, "System.AreaPath");
            if (areaPath is not null)
                metadata["areaPath"] = areaPath;

            var iterationPath = GetStringField(fields, "System.IterationPath");
            if (iterationPath is not null)
                metadata["iterationPath"] = iterationPath;

            var workItemType = GetStringField(fields, "System.WorkItemType");
            if (workItemType is not null)
                metadata["workItemType"] = workItemType;

            var candidate = new WorkCandidate
            {
                Id = $"wi_{itemId}",
                ExternalId = itemId.ToString(CultureInfo.InvariantCulture),
                ExternalUrl =
                    $"{_options.BaseUrl?.TrimEnd('/')}/{project}/_workitems/edit/{itemId}",
                RepoKey = repoKey,
                Title = GetStringField(fields, "System.Title") ?? string.Empty,
                Description = GetStringField(fields, "System.Description"),
                AcceptanceCriteria = ParseAcceptanceCriteria(
                    fields,
                    GetStringField(fields, "System.Description")
                ),
                Priority = GetIntField(fields, "Microsoft.VSTS.Common.Priority"),
                Status = GetStringField(fields, "System.State"),
                Tags = tags,
                AssignedTo = GetAssignedToDisplayName(fields),
                Source = "AzureDevOpsBoards",
                SourceMetadata = metadata.Count > 0 ? metadata : null,
            };

            results.Add(candidate);
        }

        return results;
    }

    public async Task<ClaimResult> TryClaimWorkItemAsync(
        ExternalWorkRef workRef,
        ClaimRequest request,
        CancellationToken cancellationToken
    )
    {
        var project = _options.Project;
        if (string.IsNullOrWhiteSpace(project))
        {
            return new ClaimResult
            {
                Success = false,
                FailureReason = "Azure DevOps project is not configured.",
            };
        }

        if (string.IsNullOrWhiteSpace(workRef.ExternalId))
        {
            return new ClaimResult
            {
                Success = false,
                FailureReason = "External work item identifier is required for claiming.",
            };
        }

        try
        {
            // 1. GET the current work item to read revision and tags.
            //     Plain GET without $expand — ADO returns all fields by default.
            //     Using $expand=all or $expand=minimal is unnecessary and may
            //     trigger field-selection rules that omit System.Tags.
            var getResponse = await _http.GetAsync(
                $"{project}/_apis/wit/workitems/{workRef.ExternalId}?api-version=7.1",
                cancellationToken
            );

            if (!getResponse.IsSuccessStatusCode)
            {
                return new ClaimResult
                {
                    Success = false,
                    FailureReason =
                        $"Failed to read work item {workRef.ExternalId}: "
                        + $"HTTP {(int)getResponse.StatusCode} {getResponse.ReasonPhrase}.",
                };
            }

            var getJson = await getResponse.Content.ReadAsStringAsync(cancellationToken);
            using var getDoc = JsonDocument.Parse(getJson);

            var currentRev =
                getDoc.RootElement.TryGetProperty("rev", out var revEl)
                && revEl.ValueKind == JsonValueKind.Number
                && revEl.TryGetInt32(out var rev)
                    ? rev
                    : (int?)null;

            // 2. Check if already claimed — look for agent-active or agent-worker: tags
            var currentTags = string.Empty;
            if (
                getDoc.RootElement.TryGetProperty("fields", out var fields)
                && fields.TryGetProperty("System.Tags", out var tagsEl)
                && tagsEl.ValueKind == JsonValueKind.String
            )
            {
                currentTags = tagsEl.GetString() ?? string.Empty;
            }

            var existingTags = currentTags
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (existingTags.Contains("agent-active"))
            {
                return new ClaimResult
                {
                    Success = false,
                    FailureReason =
                        $"Work item {workRef.ExternalId} is already claimed "
                        + "(has 'agent-active' tag).",
                };
            }

            // Also check for any agent-worker: tag to detect re-claim attempts
            var workerTag = existingTags.FirstOrDefault(t =>
                t.StartsWith("agent-worker:", StringComparison.OrdinalIgnoreCase)
            );
            if (workerTag is not null)
            {
                return new ClaimResult
                {
                    Success = false,
                    FailureReason =
                        $"Work item {workRef.ExternalId} is already claimed "
                        + $"by worker '{workerTag}'.",
                };
            }

            // 3. Build PATCH operations: add agent-active tag, agent-worker tag,
            //    and a claiming comment
            var newTags = string.IsNullOrWhiteSpace(currentTags)
                ? $"agent-active; agent-worker:{request.WorkerId}"
                : $"{currentTags.TrimEnd(';')}; agent-active; agent-worker:{request.WorkerId}";

            var claimedAt = request.ClaimedAt.ToString(
                "yyyy-MM-dd HH:mm:ss UTC",
                System.Globalization.CultureInfo.InvariantCulture
            );

            var patchOps = new[]
            {
                new
                {
                    op = "add",
                    path = "/fields/System.Tags",
                    value = newTags,
                },
                new
                {
                    op = "add",
                    path = "/fields/System.History",
                    value = $"Claimed by agent controller '{request.WorkerId}' at {claimedAt}.",
                },
            };

            var patchBody = JsonSerializer.Serialize(patchOps, JsonOptions);
            var patchContent = new StringContent(
                patchBody,
                Encoding.UTF8,
                "application/json-patch+json"
            );

            // 4. Use If-Match with the current revision for optimistic concurrency
            var patchRequest = new HttpRequestMessage(
                HttpMethod.Patch,
                $"{project}/_apis/wit/workitems/{workRef.ExternalId}?api-version=7.1"
            )
            {
                Content = patchContent,
            };

            if (currentRev.HasValue)
            {
                patchRequest.Headers.TryAddWithoutValidation(
                    "If-Match",
                    currentRev.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
                );
            }

            var patchResponse = await _http.SendAsync(patchRequest, cancellationToken);

            if (patchResponse.IsSuccessStatusCode)
            {
                // Extract the new revision from the response for lease tracking
                var patchJson = await patchResponse.Content.ReadAsStringAsync(cancellationToken);
                var newRev = TryExtractRevision(patchJson) ?? currentRev;

                return new ClaimResult
                {
                    Success = true,
                    WorkRef = workRef with
                    {
                        Revision = newRev?.ToString(
                            System.Globalization.CultureInfo.InvariantCulture
                        ),
                    },
                    LeaseToken =
                        $"lease_{workRef.ExternalId}_"
                        + $"{newRev}_{DateTimeOffset.UtcNow:yyyyMMddHHmmss}",
                };
            }

            if (patchResponse.StatusCode == System.Net.HttpStatusCode.PreconditionFailed)
            {
                return new ClaimResult
                {
                    Success = false,
                    FailureReason =
                        $"Work item {workRef.ExternalId} was modified "
                        + "by another process (412 Precondition Failed).",
                };
            }

            var errorBody = await patchResponse.Content.ReadAsStringAsync(cancellationToken);
            return new ClaimResult
            {
                Success = false,
                FailureReason =
                    $"Failed to claim work item {workRef.ExternalId}: "
                    + $"HTTP {(int)patchResponse.StatusCode} {patchResponse.ReasonPhrase}. "
                    + $"Details: {Truncate(errorBody, 200)}",
            };
        }
        catch (HttpRequestException ex)
        {
            return new ClaimResult
            {
                Success = false,
                FailureReason =
                    $"Network error claiming work item {workRef.ExternalId}: {ex.Message}",
            };
        }
    }

    public async Task<bool> UpdateWorkItemStatusAsync(
        ExternalWorkRef workRef,
        ExternalWorkStatus status,
        CancellationToken cancellationToken
    )
    {
        var project = _options.Project;
        if (string.IsNullOrWhiteSpace(project) || string.IsNullOrWhiteSpace(workRef.ExternalId))
            return true;

        // Build the PATCH body for work item update
        var patchOps = new List<object>();

        if (!string.IsNullOrWhiteSpace(status.Status))
        {
            patchOps.Add(
                new
                {
                    op = "add",
                    path = "/fields/System.State",
                    value = status.Status,
                }
            );
        }

        if (status.Tags is { Count: > 0 } && status.RemovedTags is null or [])
        {
            // Simple tag addition — no removals needed.
            patchOps.Add(
                new
                {
                    op = "add",
                    path = "/fields/System.Tags",
                    value = string.Join("; ", status.Tags),
                }
            );
        }

        // Track fresh revision from the tag-read GET (when RemovedTags path is taken).
        // Used as If-Match token to prevent 412 on the tag-strip PATCH.
        string? freshRev = null;

        // Handle tag removal (and optional addition): read current tags, filter,
        // merge in any new tags, and write back in a single PATCH operation.
        // The tag-read GET is load-bearing: if RemovedTags is specified and the
        // GET fails, abort entirely — no state-only PATCH masquerading as success.
        if (status.RemovedTags is { Count: > 0 })
        {
            // Fetch current work item to read existing tags.
            //     Plain GET without $expand — ADO returns all fields by default.
            //     Using $expand=minimal is unnecessary and may trigger field-selection
            //     rules that omit System.Tags in the live ADO environment.
            var getResponse = await _http.GetAsync(
                $"{project}/_apis/wit/workitems/{workRef.ExternalId}?api-version=7.1",
                cancellationToken
            );

            // [rework_reactivate_get] — structured log for the tag-read GET.
            // Lets an operator confirm whether the GET is succeeding and whether
            // System.Tags is present in the response.
            var tagsPresent = false;
            int? getRev = null;
            int tagCount = 0;

            if (!getResponse.IsSuccessStatusCode)
            {
                Log.ReworkReactivateGetFailed(
                    _logger,
                    workRef.ExternalId,
                    (int)getResponse.StatusCode,
                    getResponse.ReasonPhrase ?? "(no reason phrase)"
                );

                // Load-bearing GET failed — abort the PATCH entirely.
                // Returning false so the caller (e.g. reactivation) knows the
                // tag operations were NOT applied.
                return false;
            }

            var getJson = await getResponse.Content.ReadAsStringAsync(cancellationToken);
            using var getDoc = JsonDocument.Parse(getJson);

            // Capture the freshly-read revision from this GET so the PATCH
            // uses the current rev, not a possibly-stale workRef.Revision.
            // This prevents 412 Precondition Failed on the tag-strip PATCH.
            freshRev =
                getDoc.RootElement.TryGetProperty("rev", out var freshRevEl)
                && freshRevEl.ValueKind == JsonValueKind.Number
                && freshRevEl.TryGetInt32(out var freshRevInt)
                    ? freshRevInt.ToString(CultureInfo.InvariantCulture)
                    : null;
            if (!string.IsNullOrWhiteSpace(freshRev))
                getRev = int.Parse(freshRev, CultureInfo.InvariantCulture);

            var currentTags = string.Empty;
            if (
                getDoc.RootElement.TryGetProperty("fields", out var fields)
                && fields.TryGetProperty("System.Tags", out var tagsEl)
                && tagsEl.ValueKind == JsonValueKind.String
            )
            {
                currentTags = tagsEl.GetString() ?? string.Empty;
                tagsPresent = true;
            }

            var existingTags = currentTags
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            tagCount = existingTags.Count;

            Log.ReworkReactivateGet(
                _logger,
                workRef.ExternalId,
                (int)getResponse.StatusCode,
                tagsPresent,
                getRev,
                tagCount
            );

            // Remove matching tags (supports exact match and prefix:* wildcard).
            foreach (var tagToRemove in status.RemovedTags)
            {
                var trimmed = tagToRemove?.Trim();
                if (string.IsNullOrWhiteSpace(trimmed))
                    continue;

                if (trimmed.EndsWith(":*", StringComparison.OrdinalIgnoreCase))
                {
                    var prefix = trimmed[..^1]; // strip the '*', keep the ':'
                    existingTags.RemoveWhere(t =>
                        t.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    );
                }
                else
                {
                    existingTags.RemoveWhere(t =>
                        t.Equals(trimmed, StringComparison.OrdinalIgnoreCase)
                    );
                }
            }

            // Merge in any tags to add.
            if (status.Tags is { Count: > 0 })
            {
                foreach (var tagToAdd in status.Tags)
                {
                    existingTags.Add(tagToAdd);
                }
            }

            var newTags =
                existingTags.Count > 0
                    ? string.Join(
                        "; ",
                        existingTags.OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
                    )
                    : string.Empty;

            patchOps.Add(
                new
                {
                    op = "add",
                    path = "/fields/System.Tags",
                    value = newTags,
                }
            );
        }

        if (patchOps.Count == 0)
            return true;

        var body = JsonSerializer.Serialize(patchOps, JsonOptions);
        var content = new StringContent(body, Encoding.UTF8, "application/json-patch+json");

        var request = new HttpRequestMessage(
            HttpMethod.Patch,
            $"{project}/_apis/wit/workitems/{workRef.ExternalId}?api-version=7.1"
        )
        {
            Content = content,
        };

        // Use If-Match with revision for optimistic concurrency when available.
        // Prefer freshRev (captured from the tag-read GET) over workRef.Revision
        // to prevent 412 Precondition Failed from stale revision tokens on
        // RemovedTags-bearing PATCHes.
        var ifMatchRev = !string.IsNullOrWhiteSpace(freshRev) ? freshRev : workRef.Revision;
        if (!string.IsNullOrWhiteSpace(ifMatchRev))
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatchRev);
        }

        // [rework_reactivate_patch] — structured log for the PATCH submission.
        // Lets an operator confirm the rev used for If-Match, the RemovedTags set,
        // the Tags set, and the target state.
        if (status.RemovedTags is { Count: > 0 })
        {
            Log.ReworkReactivatePatch(
                _logger,
                workRef.ExternalId,
                ifMatchRev,
                status.RemovedTags,
                status.Tags,
                status.Status
            );
        }

        var response = await _http.SendAsync(request, cancellationToken);

        // 412 Precondition Failed: concurrent modification.
        // For RemovedTags-bearing PATCHes (reactivation path) fail loudly so the
        // caller can surface the failure and retry on the next poll.
        // Status-only projections retain best-effort behavior.
        if (response.StatusCode == System.Net.HttpStatusCode.PreconditionFailed)
        {
            if (status.RemovedTags is { Count: > 0 })
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                Log.ReworkReactivatePatchFailed(
                    _logger,
                    workRef.ExternalId,
                    (int)response.StatusCode,
                    Truncate(errorBody, 200)
                );
                return false;
            }
            // Best-effort status projection: concurrent modification is not fatal.
            return true;
        }

        if (!response.IsSuccessStatusCode)
        {
            if (status.RemovedTags is { Count: > 0 })
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                Log.ReworkReactivatePatchFailed(
                    _logger,
                    workRef.ExternalId,
                    (int)response.StatusCode,
                    Truncate(errorBody, 200)
                );
            }
            return false;
        }

        return true;
    }

    public async Task AddCommentAsync(
        ExternalWorkRef workRef,
        string comment,
        CancellationToken cancellationToken
    )
    {
        var project = _options.Project;
        if (string.IsNullOrWhiteSpace(project) || string.IsNullOrWhiteSpace(workRef.ExternalId))
            return;

        // Azure DevOps doesn't have a simple "add comment" REST API for work items.
        // Comments are added by including a System.History field update in the PATCH body.
        var patchOps = new[]
        {
            new
            {
                op = "add",
                path = "/fields/System.History",
                value = comment,
            },
        };

        var body = JsonSerializer.Serialize(patchOps, JsonOptions);
        var content = new StringContent(body, Encoding.UTF8, "application/json-patch+json");

        var request = new HttpRequestMessage(
            HttpMethod.Patch,
            $"{project}/_apis/wit/workitems/{workRef.ExternalId}?api-version=7.1"
        )
        {
            Content = content,
        };

        // Use If-Match with revision for optimistic concurrency when available
        if (!string.IsNullOrWhiteSpace(workRef.Revision))
        {
            request.Headers.TryAddWithoutValidation("If-Match", workRef.Revision);
        }

        var response = await _http.SendAsync(request, cancellationToken);

        // 412 Precondition Failed: best-effort comment projection.
        // Concurrent modifications are not fatal for comments.
        if (response.StatusCode == System.Net.HttpStatusCode.PreconditionFailed)
        {
            return;
        }

        response.EnsureSuccessStatusCode();
    }

    public async Task<IReadOnlyList<WorkItemComment>> GetCommentsAsync(
        ExternalWorkRef workRef,
        int maxComments,
        CancellationToken cancellationToken
    )
    {
        var project = _options.Project;
        if (string.IsNullOrWhiteSpace(project) || string.IsNullOrWhiteSpace(workRef.ExternalId))
            return [];

        // Fetch threads (discussion history) for the work item.
        // ADO threads API: GET {project}/_apis/wit/workitems/{id}/threads?api-version=7.1
        var response = await _http.GetAsync(
            $"{project}/_apis/wit/workitems/{workRef.ExternalId}/threads?api-version=7.1",
            cancellationToken
        );

        if (!response.IsSuccessStatusCode)
        {
            // Best-effort: missing comments do not block the run.
            return [];
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(json);

        var comments = new List<WorkItemComment>();

        if (
            doc.RootElement.TryGetProperty("value", out var valueArray)
            && valueArray.ValueKind == JsonValueKind.Array
        )
        {
            foreach (var thread in valueArray.EnumerateArray())
            {
                // Each thread contains a "comments" array.
                if (
                    !thread.TryGetProperty("comments", out var commentsArray)
                    || commentsArray.ValueKind != JsonValueKind.Array
                )
                {
                    continue;
                }

                foreach (var comment in commentsArray.EnumerateArray())
                {
                    if (comments.Count >= maxComments)
                        break;

                    // Extract comment text
                    var text =
                        comment.TryGetProperty("comment", out var commentEl)
                        && commentEl.ValueKind == JsonValueKind.String
                            ? commentEl.GetString() ?? string.Empty
                            : string.Empty;

                    // Extract author display name
                    string? author = null;
                    if (
                        comment.TryGetProperty("author", out var authorEl)
                        && authorEl.ValueKind == JsonValueKind.Object
                        && authorEl.TryGetProperty("displayName", out var displayNameEl)
                    )
                    {
                        author = displayNameEl.GetString();
                    }

                    // Extract posted date
                    DateTimeOffset? postedAt = null;
                    if (
                        comment.TryGetProperty("publishedDate", out var dateEl)
                        && dateEl.ValueKind == JsonValueKind.String
                        && DateTimeOffset.TryParse(dateEl.GetString(), out var parsedDate)
                    )
                    {
                        postedAt = parsedDate;
                    }

                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        comments.Add(
                            new WorkItemComment
                            {
                                Author = author,
                                Text = text.Trim(),
                                PostedAt = postedAt,
                            }
                        );
                    }
                }

                if (comments.Count >= maxComments)
                    break;
            }
        }

        return comments;
    }

    public async Task<IReadOnlyList<RepositoryInfo>> ListRepositoriesAsync(
        string project,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(project))
        {
            throw new InvalidOperationException(
                "Azure DevOps project name is required for repository listing."
            );
        }

        var response = await _http.GetAsync(
            $"{project}/_apis/git/repositories?api-version=7.1",
            cancellationToken
        );

        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(json);

        var repositories = new List<RepositoryInfo>();

        if (
            doc.RootElement.TryGetProperty("value", out var valueArray)
            && valueArray.ValueKind == JsonValueKind.Array
        )
        {
            foreach (var repo in valueArray.EnumerateArray())
            {
                var id =
                    repo.TryGetProperty("id", out var idEl)
                    && idEl.ValueKind == JsonValueKind.String
                        ? idEl.GetString() ?? string.Empty
                        : string.Empty;

                var name =
                    repo.TryGetProperty("name", out var nameEl)
                    && nameEl.ValueKind == JsonValueKind.String
                        ? nameEl.GetString() ?? string.Empty
                        : string.Empty;

                string? defaultBranch = null;
                if (
                    repo.TryGetProperty("defaultBranch", out var dbEl)
                    && dbEl.ValueKind == JsonValueKind.String
                )
                {
                    defaultBranch = dbEl.GetString();
                }

                string? remoteUrl = null;
                if (
                    repo.TryGetProperty("remoteUrl", out var ruEl)
                    && ruEl.ValueKind == JsonValueKind.String
                )
                {
                    remoteUrl = ruEl.GetString();
                }
                else if (
                    repo.TryGetProperty("webUrl", out var wuEl)
                    && wuEl.ValueKind == JsonValueKind.String
                )
                {
                    // Fallback: use webUrl if remoteUrl is not present
                    remoteUrl = wuEl.GetString();
                }

                repositories.Add(
                    new RepositoryInfo
                    {
                        Id = id,
                        Name = name,
                        DefaultBranch = defaultBranch,
                        RemoteUrl = remoteUrl,
                    }
                );
            }
        }

        return repositories;
    }

    public async Task ReleaseClaimWorkItemAsync(
        ReleaseClaimRequest request,
        CancellationToken cancellationToken
    )
    {
        var project = _options.Project;
        if (
            string.IsNullOrWhiteSpace(project)
            || string.IsNullOrWhiteSpace(request.WorkRef.ExternalId)
        )
            return;

        try
        {
            // 1. GET the current work item to read revision and tags.
            //     Plain GET without $expand — ADO returns all fields by default.
            var getResponse = await _http.GetAsync(
                $"{project}/_apis/wit/workitems/{request.WorkRef.ExternalId}?api-version=7.1",
                cancellationToken
            );

            if (!getResponse.IsSuccessStatusCode)
            {
                // Best-effort: if we can't read the work item, we can't release the claim.
                return;
            }

            var getJson = await getResponse.Content.ReadAsStringAsync(cancellationToken);
            using var getDoc = JsonDocument.Parse(getJson);

            var currentRev =
                getDoc.RootElement.TryGetProperty("rev", out var revEl)
                && revEl.ValueKind == JsonValueKind.Number
                && revEl.TryGetInt32(out var rev)
                    ? rev
                    : (int?)null;

            // 2. Read current tags and strip agent-controlled tags
            var currentTags = string.Empty;
            if (
                getDoc.RootElement.TryGetProperty("fields", out var fields)
                && fields.TryGetProperty("System.Tags", out var tagsEl)
                && tagsEl.ValueKind == JsonValueKind.String
            )
            {
                currentTags = tagsEl.GetString() ?? string.Empty;
            }

            var existingTags = currentTags
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();

            // Strip agent-active and any agent-worker:* tags
            existingTags.RemoveAll(t =>
                t.Equals("agent-active", StringComparison.OrdinalIgnoreCase)
                || t.StartsWith("agent-worker:", StringComparison.OrdinalIgnoreCase)
            );

            var newTags = existingTags.Count > 0 ? string.Join("; ", existingTags) : string.Empty;

            // 3. Build PATCH operations: update tags, optionally revert state, add history
            var patchOps = new List<object>
            {
                new
                {
                    op = "add",
                    path = "/fields/System.Tags",
                    value = newTags,
                },
            };

            // Optionally revert to target state (e.g. "New")
            if (!string.IsNullOrWhiteSpace(request.TargetState))
            {
                patchOps.Add(
                    new
                    {
                        op = "add",
                        path = "/fields/System.State",
                        value = request.TargetState,
                    }
                );
            }

            // Add a history entry explaining the release
            var releasedAt = DateTimeOffset.UtcNow.ToString(
                "yyyy-MM-dd HH:mm:ss UTC",
                System.Globalization.CultureInfo.InvariantCulture
            );
            var reason = string.IsNullOrWhiteSpace(request.Reason)
                ? "Claim released by agent controller."
                : $"Claim released by agent controller: {request.Reason}";

            patchOps.Add(
                new
                {
                    op = "add",
                    path = "/fields/System.History",
                    value = $"[{releasedAt}] {reason}",
                }
            );

            var body = JsonSerializer.Serialize(patchOps, JsonOptions);
            var content = new StringContent(body, Encoding.UTF8, "application/json-patch+json");

            var patchRequest = new HttpRequestMessage(
                HttpMethod.Patch,
                $"{project}/_apis/wit/workitems/{request.WorkRef.ExternalId}?api-version=7.1"
            )
            {
                Content = content,
            };

            if (currentRev.HasValue)
            {
                patchRequest.Headers.TryAddWithoutValidation(
                    "If-Match",
                    currentRev.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
                );
            }

            var patchResponse = await _http.SendAsync(patchRequest, cancellationToken);

            // 412 Precondition Failed: concurrent modification — best-effort, not fatal.
            if (patchResponse.StatusCode == System.Net.HttpStatusCode.PreconditionFailed)
            {
                return;
            }

            patchResponse.EnsureSuccessStatusCode();
        }
        catch
        {
            // Best-effort: claim release failures are not fatal.
            // The work item may be recovered on the next poll cycle via stale-run recovery.
        }
    }

    public void Dispose()
    {
        _http.Dispose();
    }

    // ─── WIQL builder ────────────────────────────────────

    private static string BuildWiql(string project, BoardsQueryParameters parameters)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"SELECT [System.Id] FROM WorkItems");
        sb.Append(
            CultureInfo.InvariantCulture,
            $" WHERE [System.TeamProject] = '{EscapeWiql(project)}'"
        );

        if (parameters.States is { Count: > 0 })
        {
            var states = string.Join(", ", parameters.States.Select(s => $"'{EscapeWiql(s)}'"));
            sb.Append(CultureInfo.InvariantCulture, $" AND [System.State] IN ({states})");
        }

        if (parameters.ExcludedStates is { Count: > 0 })
        {
            var excludedStates = string.Join(
                ", ",
                parameters.ExcludedStates.Select(state => $"'{EscapeWiql(state)}'")
            );
            sb.Append(
                CultureInfo.InvariantCulture,
                $" AND [System.State] NOT IN ({excludedStates})"
            );
        }

        if (parameters.Tags is { Count: > 0 })
        {
            foreach (var tag in parameters.Tags)
            {
                sb.Append(
                    CultureInfo.InvariantCulture,
                    $" AND [System.Tags] CONTAINS '{EscapeWiql(tag)}'"
                );
            }
        }

        if (parameters.ExcludedTags is { Count: > 0 })
        {
            foreach (var tag in parameters.ExcludedTags)
            {
                sb.Append(
                    CultureInfo.InvariantCulture,
                    $" AND [System.Tags] NOT CONTAINS '{EscapeWiql(tag)}'"
                );
            }
        }

        sb.Append(
            CultureInfo.InvariantCulture,
            $" ORDER BY [Microsoft.VSTS.Common.Priority] ASC, [System.CreatedDate] DESC"
        );

        return sb.ToString();
    }

    private static string EscapeWiql(string value) => value.Replace("'", "''");

    // ─── Concurrency helpers ────────────────────────────

    /// <summary>
    /// Attempts to extract the <c>rev</c> field from a JSON response body.
    /// Returns <c>null</c> when the field is missing or not a valid integer.
    /// </summary>
    private static int? TryExtractRevision(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (
                doc.RootElement.TryGetProperty("rev", out var revEl)
                && revEl.ValueKind == JsonValueKind.Number
                && revEl.TryGetInt32(out var rev)
            )
            {
                return rev;
            }
        }
        catch (JsonException)
        {
            // Response body is not valid JSON — revision is unavailable.
        }

        return null;
    }

    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
            return value;

        return value[..maxLength] + "...";
    }

    // ─── RepoKey resolution ──────────────────────────────

    /// <summary>
    /// Resolves a repository key from work item tags.
    /// Looks for a tag with the prefix <c>repo:</c> and returns the
    /// remainder as the repository key.
    /// Returns <see cref="string.Empty"/> when no matching tag is found.
    /// </summary>
    private static string ResolveRepoKeyFromTags(string[] tags)
    {
        const string repoPrefix = "repo:";

        for (int i = 0; i < tags.Length; i++)
        {
            var tag = tags[i];
            if (tag.StartsWith(repoPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var key = tag[repoPrefix.Length..].Trim();
                if (!string.IsNullOrWhiteSpace(key))
                    return key;
            }
        }

        return string.Empty;
    }

    public async Task<AzureDevOpsConnectivityResult> VerifyConnectivityAsync(
        string organizationUrl,
        string project,
        string personalAccessToken,
        CancellationToken cancellationToken
    )
    {
        // Build a dedicated HttpClient for this one-off connectivity check.
        // The injected _http is scoped to the configured org/PAT from options;
        // this method receives explicit credentials so it must construct its own.
        using var http = new HttpClient();
        http.BaseAddress = new Uri(organizationUrl.TrimEnd('/') + "/");

        var authBytes = Encoding.ASCII.GetBytes($":{personalAccessToken}");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(authBytes)
        );

        try
        {
            // (1) Lightweight test: GET project info (validates org URL + PAT + project)
            using var result = await http.GetAsync(
                $"_apis/projects/{project}?api-version=7.1",
                cancellationToken
            );

            var statusCode = result.StatusCode;

            if (!result.IsSuccessStatusCode)
            {
                var errorBody = await result.Content.ReadAsStringAsync(cancellationToken);
                var error =
                    $"HTTP {(int)result.StatusCode} {result.ReasonPhrase}: "
                    + (errorBody.Length > 200 ? errorBody[..200] + "..." : errorBody);
                return new AzureDevOpsConnectivityResult
                {
                    Success = false,
                    Status = statusCode,
                    Error = error,
                };
            }

            // (2) Enumerate repositories after successful connectivity test
            var repositories = await FetchRepositoriesAsync(http, project, cancellationToken);

            return new AzureDevOpsConnectivityResult
            {
                Success = true,
                Status = statusCode,
                Repositories = repositories,
            };
        }
        catch (OperationCanceledException)
        {
            return new AzureDevOpsConnectivityResult
            {
                Success = false,
                Error = "Request timed out or was cancelled.",
            };
        }
        catch (HttpRequestException ex)
        {
            return new AzureDevOpsConnectivityResult
            {
                Success = false,
                Error = $"HTTP request failed: {ex.Message}",
            };
        }
        catch (Exception ex)
        {
            return new AzureDevOpsConnectivityResult
            {
                Success = false,
                Error = $"Unexpected error: {ex.Message}",
            };
        }
    }

    public async Task<IReadOnlyList<string>> GetValidStatesAsync(
        string project,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(project))
        {
            return Array.Empty<string>();
        }

        // Use a default work item type for state discovery.
        // States are process-level, not WIT-specific in most configurations.
        const string discoveryWorkItemType = "User Story";

        try
        {
            // (1) Find the process for the project by querying project details.
            //     The project response includes a "processId" GUID.
            var projectResponse = await _http.GetAsync(
                $"_apis/projects/{Uri.EscapeDataString(project)}?api-version=7.1&includeProcessSettings=true",
                cancellationToken
            );

            if (!projectResponse.IsSuccessStatusCode)
            {
                return Array.Empty<string>();
            }

            var projectJson = await projectResponse.Content.ReadAsStringAsync(cancellationToken);
            using var projectDoc = JsonDocument.Parse(projectJson);

            // Extract processId from the project response.
            var processId =
                projectDoc.RootElement.TryGetProperty("processSettings", out var processSettings)
                && processSettings.TryGetProperty("processId", out var processIdEl)
                && processIdEl.ValueKind == JsonValueKind.String
                    ? processIdEl.GetString()
                    : null;

            if (string.IsNullOrWhiteSpace(processId))
            {
                // Fallback: try top-level processId (some API versions place it there).
                processId =
                    projectDoc.RootElement.TryGetProperty("processId", out var topProcessIdEl)
                    && topProcessIdEl.ValueKind == JsonValueKind.String
                        ? topProcessIdEl.GetString()
                        : null;
            }

            if (string.IsNullOrWhiteSpace(processId))
            {
                // Cannot determine process — enumerate processes and find the one for this project.
                var processesResponse = await _http.GetAsync(
                    "_apis/work/processes?api-version=7.1-preview.3",
                    cancellationToken
                );

                if (!processesResponse.IsSuccessStatusCode)
                {
                    return Array.Empty<string>();
                }

                var processesJson = await processesResponse.Content.ReadAsStringAsync(
                    cancellationToken
                );
                using var processesDoc = JsonDocument.Parse(processesJson);

                // Find the process associated with this project.
                if (
                    processesDoc.RootElement.TryGetProperty("value", out var processesArray)
                    && processesArray.ValueKind == JsonValueKind.Array
                )
                {
                    foreach (var process in processesArray.EnumerateArray())
                    {
                        if (
                            process.TryGetProperty("id", out var pidEl)
                            && pidEl.ValueKind == JsonValueKind.String
                        )
                        {
                            var pid = pidEl.GetString();
                            // Check if this process has the project.
                            if (
                                process.TryGetProperty("defaultTemplate", out var templateEl)
                                && templateEl.ValueKind == JsonValueKind.Object
                                && templateEl.TryGetProperty("projectPools", out var poolsEl)
                                && poolsEl.ValueKind == JsonValueKind.Array
                            )
                            {
                                foreach (var pool in poolsEl.EnumerateArray())
                                {
                                    if (
                                        pool.TryGetProperty("name", out var poolNameEl)
                                        && poolNameEl.ValueKind == JsonValueKind.String
                                        && poolNameEl
                                            .GetString()!
                                            .Equals(project, StringComparison.OrdinalIgnoreCase)
                                    )
                                    {
                                        processId = pid;
                                        break;
                                    }
                                }
                            }

                            if (!string.IsNullOrWhiteSpace(processId))
                                break;
                        }
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(processId))
            {
                return Array.Empty<string>();
            }

            // (2) Get the work item type definition to find System.State allowed values.
            var witResponse = await _http.GetAsync(
                $"_apis/work/processes/{Uri.EscapeDataString(processId)}/workItemTypes/{Uri.EscapeDataString(discoveryWorkItemType)}?api-version=7.1-preview.3&fields=System.State",
                cancellationToken
            );

            if (!witResponse.IsSuccessStatusCode)
            {
                return Array.Empty<string>();
            }

            var witJson = await witResponse.Content.ReadAsStringAsync(cancellationToken);
            using var witDoc = JsonDocument.Parse(witJson);

            // Navigate to fields -> System.State -> name -> allowedValues
            var allowedValues = new List<string>();

            if (
                witDoc.RootElement.TryGetProperty("fields", out var fields)
                && fields.ValueKind == JsonValueKind.Object
                && fields.TryGetProperty("System.State", out var stateField)
                && stateField.ValueKind == JsonValueKind.Object
                && stateField.TryGetProperty("name", out var nameField)
                && nameField.ValueKind == JsonValueKind.Object
                && nameField.TryGetProperty("allowedValues", out var allowedValuesEl)
                && allowedValuesEl.ValueKind == JsonValueKind.Array
            )
            {
                foreach (var value in allowedValuesEl.EnumerateArray())
                {
                    if (value.ValueKind == JsonValueKind.String)
                    {
                        var state = value.GetString();
                        if (!string.IsNullOrWhiteSpace(state))
                        {
                            allowedValues.Add(state);
                        }
                    }
                }
            }

            return allowedValues;
        }
        catch (OperationCanceledException)
        {
            return Array.Empty<string>();
        }
        catch (HttpRequestException)
        {
            return Array.Empty<string>();
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Fetches Git repositories for a project. Returns an empty list on failure
    /// rather than throwing — the caller is responsible for error handling.
    /// </summary>
    private static async Task<IReadOnlyList<RepositoryInfo>> FetchRepositoriesAsync(
        HttpClient http,
        string project,
        CancellationToken cancellationToken
    )
    {
        try
        {
            using var reposResult = await http.GetAsync(
                $"{project}/_apis/git/repositories?api-version=7.1",
                cancellationToken
            );

            if (!reposResult.IsSuccessStatusCode)
            {
                // Repository listing failed — return empty list.
                // The connectivity itself succeeded (project endpoint worked).
                return Array.Empty<RepositoryInfo>();
            }

            var json = await reposResult.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);

            var repositories = new List<RepositoryInfo>();

            if (
                doc.RootElement.TryGetProperty("value", out var valueArray)
                && valueArray.ValueKind == JsonValueKind.Array
            )
            {
                foreach (var repo in valueArray.EnumerateArray())
                {
                    var id =
                        repo.TryGetProperty("id", out var idEl)
                        && idEl.ValueKind == JsonValueKind.String
                            ? idEl.GetString() ?? string.Empty
                            : string.Empty;

                    var name =
                        repo.TryGetProperty("name", out var nameEl)
                        && nameEl.ValueKind == JsonValueKind.String
                            ? nameEl.GetString() ?? string.Empty
                            : string.Empty;

                    string? defaultBranch = null;
                    if (
                        repo.TryGetProperty("defaultBranch", out var dbEl)
                        && dbEl.ValueKind == JsonValueKind.String
                    )
                    {
                        defaultBranch = dbEl.GetString();
                    }

                    string? remoteUrl = null;
                    if (
                        repo.TryGetProperty("remoteUrl", out var ruEl)
                        && ruEl.ValueKind == JsonValueKind.String
                    )
                    {
                        remoteUrl = ruEl.GetString();
                    }

                    repositories.Add(
                        new RepositoryInfo
                        {
                            Id = id,
                            Name = name,
                            DefaultBranch = defaultBranch,
                            RemoteUrl = remoteUrl,
                        }
                    );
                }
            }

            return repositories;
        }
        catch (OperationCanceledException)
        {
            return Array.Empty<RepositoryInfo>();
        }
        catch (HttpRequestException)
        {
            return Array.Empty<RepositoryInfo>();
        }
        catch
        {
            return Array.Empty<RepositoryInfo>();
        }
    }

    // ─── Acceptance criteria parsing ──────────────────────

    /// <summary>
    /// Extract acceptance criteria from an ADO work item.
    ///
    /// Priority:
    /// 1. <c>Microsoft.VSTS.Common.AcceptanceCriteria</c> field (Agile process template).
    /// 2. Markdown checklist items (<c>- [ ]</c> / <c>- [x]</c>) in the description.
    /// 3. HTML checkbox list items (<c>&lt;input type="checkbox"&gt;</c>) in the description
    ///    (ADO rich-text editor renders checklists as HTML).
    ///
    /// Returns <c>null</c> when no acceptance criteria are found.
    /// </summary>
    private static Dictionary<string, string>? ParseAcceptanceCriteria(
        JsonElement fields,
        string? description
    )
    {
        // (1) Try the dedicated AcceptanceCriteria field first.
        //     This field exists in the Agile process template and some others.
        var acField = GetStringField(fields, "Microsoft.VSTS.Common.AcceptanceCriteria");
        if (!string.IsNullOrWhiteSpace(acField))
        {
            var parsed = ParseAcceptanceCriteriaText(acField);
            if (parsed is not null)
                return parsed;
        }

        // (2) Fall back to parsing checklist items from the description.
        if (!string.IsNullOrWhiteSpace(description))
        {
            var parsed = ParseChecklistItems(description);
            if (parsed is not null)
                return parsed;
        }

        return null;
    }

    /// <summary>
    /// Parse acceptance criteria from plain text that may contain
    /// markdown checklist items or newline-separated criteria.
    /// </summary>
    private static Dictionary<string, string>? ParseAcceptanceCriteriaText(string text)
    {
        // First try markdown checklist pattern
        var checklistItems = ParseChecklistItems(text);
        if (checklistItems is not null)
            return checklistItems;

        // Fall back to newline-separated non-empty lines
        var lines = text.Split(
                '\n',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
            )
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToList();

        if (lines.Count == 0)
            return null;

        var dict = new Dictionary<string, string>();
        for (int i = 0; i < lines.Count; i++)
        {
            dict[(i + 1).ToString(CultureInfo.InvariantCulture)] = lines[i].Trim();
        }

        return dict;
    }

    /// <summary>
    /// Extract checklist items from text that may contain:
    /// - Markdown checklists: <c>- [ ] text</c> or <c>- [x] text</c>
    /// - HTML checkbox lists (ADO rich text): <c>&lt;input type="checkbox"&gt; text</c>
    ///
    /// Returns <c>null</c> when no checklist items are found.
    /// </summary>
    private static Dictionary<string, string>? ParseChecklistItems(string text)
    {
        var items = new List<string>();

        // Pattern 1: Markdown checklist items (- [ ] or - [x])
        // [xX]? makes the checkmark optional; \s*? minimizes whitespace before ].
        var markdownPattern = new Regex(
            "^\\s*-\\s*\\[[xX]?\\s*?\\]\\s+(.+)$",
            RegexOptions.Multiline | RegexOptions.Compiled
        );

        foreach (Match match in markdownPattern.Matches(text))
        {
            items.Add(match.Groups[1].Value.Trim());
        }

        // Pattern 2: HTML checkbox items (ADO rich text editor output)
        // Matches: <input type="checkbox" ...> text or <input checked="..." type="checkbox" ...> text
        if (items.Count == 0)
        {
            var htmlPattern = new Regex(
                "<input[^>]*type=[\"']checkbox[\"'][^>]*>\\s*(.+?)?(?:</li>|<br[^>]*>|$)",
                RegexOptions.IgnoreCase | RegexOptions.Compiled
            );

            foreach (Match match in htmlPattern.Matches(text))
            {
                var criterion = match.Groups[1].Value.Trim();
                // Strip any remaining HTML tags from the criterion text
                criterion = Regex.Replace(criterion, "<[^>]+>", string.Empty).Trim();
                if (!string.IsNullOrWhiteSpace(criterion))
                {
                    items.Add(criterion);
                }
            }
        }

        if (items.Count == 0)
            return null;

        var dict = new Dictionary<string, string>();
        for (int i = 0; i < items.Count; i++)
        {
            dict[(i + 1).ToString(CultureInfo.InvariantCulture)] = items[i];
        }

        return dict;
    }

    // ─── Field helpers ────────────────────────────────────

    private static string? GetStringField(JsonElement fields, string name)
    {
        return
            fields.TryGetProperty(name, out var element)
            && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;
    }

    private static int? GetIntField(JsonElement fields, string name)
    {
        if (fields.TryGetProperty(name, out var element))
        {
            if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var val))
                return val;
            if (
                element.ValueKind == JsonValueKind.String
                && int.TryParse(element.GetString(), out var strVal)
            )
                return strVal;
        }

        return null;
    }

    private static string? GetAssignedToDisplayName(JsonElement fields)
    {
        if (fields.TryGetProperty("System.AssignedTo", out var assignedTo))
        {
            if (
                assignedTo.ValueKind == JsonValueKind.Object
                && assignedTo.TryGetProperty("displayName", out var displayName)
            )
            {
                return displayName.GetString();
            }
            if (assignedTo.ValueKind == JsonValueKind.String)
            {
                return assignedTo.GetString();
            }
        }

        return null;
    }

    /// <summary>
    /// Source-generated structured log markers for the reactivation PATCH flow.
    /// Stable log marker strings ([rework_reactivate_*]) allow operators to
    /// confirm whether the tag-read GET is succeeding, whether the tag op is
    /// present in the PATCH, and whether ADO accepted it.
    /// </summary>
    private static partial class Log
    {
        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "[rework_reactivate_get] WorkItem={WorkItemId} Status={StatusCode} "
                + "TagsPresent={TagsPresent} Rev={Rev} TagCount={TagCount}"
        )]
        public static partial void ReworkReactivateGet(
            ILogger logger,
            string workItemId,
            int statusCode,
            bool tagsPresent,
            int? rev,
            int tagCount
        );

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "[rework_reactivate_get_failed] WorkItem={WorkItemId} "
                + "Status={StatusCode} Reason={Reason} — aborting PATCH (load-bearing GET failed)"
        )]
        public static partial void ReworkReactivateGetFailed(
            ILogger logger,
            string workItemId,
            int statusCode,
            string reason
        );

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "[rework_reactivate_patch] WorkItem={WorkItemId} "
                + "IfMatchRev={IfMatchRev} RemovedTags={RemovedTags} "
                + "Tags={Tags} State={State}"
        )]
        public static partial void ReworkReactivatePatch(
            ILogger logger,
            string workItemId,
            string? ifMatchRev,
            IReadOnlyList<string>? removedTags,
            IReadOnlyList<string>? tags,
            string? state
        );

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "[rework_reactivate_patch_failed] WorkItem={WorkItemId} "
                + "Status={StatusCode} Error={Error}"
        )]
        public static partial void ReworkReactivatePatchFailed(
            ILogger logger,
            string workItemId,
            int statusCode,
            string error
        );
    }
}

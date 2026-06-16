using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AgentController.Application;
using AgentController.Domain;
using AgentController.Infrastructure.Options;

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
internal sealed class AzureDevOpsBoardsClient : IAzureDevOpsBoardsClient
{
    private readonly HttpClient _http;
    private readonly AzureDevOpsBoardsOptions _options;

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
        "System.AreaPath",
        "System.IterationPath",
        "System.WorkItemType",
    ];

    public AzureDevOpsBoardsClient(HttpClient http, AzureDevOpsBoardsOptions options)
    {
        _http = http;
        _options = options;

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
            _http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Basic", Convert.ToBase64String(authBytes));
        }
    }

    public async Task<IReadOnlyList<WorkCandidate>> QueryWorkItemsAsync(
        BoardsQueryParameters parameters,
        CancellationToken cancellationToken)
    {
        var project = string.IsNullOrWhiteSpace(parameters.Project)
            ? _options.Project
            : parameters.Project;

        if (string.IsNullOrWhiteSpace(project))
        {
            throw new InvalidOperationException(
                "Azure DevOps project name is required for work item queries. " +
                "Configure 'workSource:project' or supply it in the query parameters.");
        }

        // Build WIQL query
        var wiql = BuildWiql(project, parameters);
        var wiqlBody = JsonSerializer.Serialize(new { query = wiql }, JsonOptions);
        var content = new StringContent(wiqlBody, Encoding.UTF8, "application/json");

        // POST WIQL query
        var wiqlResponse = await _http.PostAsync(
            $"{project}/_apis/wit/wiql?api-version=7.1",
            content,
            cancellationToken);

        wiqlResponse.EnsureSuccessStatusCode();

        var wiqlJson = await wiqlResponse.Content.ReadAsStringAsync(cancellationToken);
        using var wiqlDoc = JsonDocument.Parse(wiqlJson);
        var workItemRefs = wiqlDoc.RootElement
            .GetProperty("workItems")
            .EnumerateArray()
            .ToList();

        if (workItemRefs.Count == 0)
            return [];

        // Batch fetch work item details
        var ids = workItemRefs
            .Select(r => r.GetProperty("id").GetInt32())
            .ToList();

        var batchBody = JsonSerializer.Serialize(
            new
            {
                ids,
                fields = WorkItemFields,
            },
            JsonOptions);

        var batchContent = new StringContent(batchBody, Encoding.UTF8, "application/json");
        var batchResponse = await _http.PostAsync(
            $"{project}/_apis/wit/workitemsbatch?api-version=7.1",
            batchContent,
            cancellationToken);

        batchResponse.EnsureSuccessStatusCode();

        var batchJson = await batchResponse.Content.ReadAsStringAsync(cancellationToken);
        using var batchDoc = JsonDocument.Parse(batchJson);
        var workItems = batchDoc.RootElement.GetProperty("value").EnumerateArray();

        var results = new List<WorkCandidate>();
        foreach (var item in workItems)
        {
            var itemId = item.GetProperty("id").GetInt32();

            // Extract revision for optimistic concurrency on later updates
            var revision = item.TryGetProperty("rev", out var revEl)
                           && revEl.ValueKind == JsonValueKind.Number
                           && revEl.TryGetInt32(out var rev)
                ? rev.ToString(CultureInfo.InvariantCulture)
                : null;

            // Require the fields object for mapping
            if (!item.TryGetProperty("fields", out var fields)
                || fields.ValueKind != JsonValueKind.Object)
            {
                // Malformed: work item returned without fields — skip with warning
                continue;
            }

            // Parse tags
            var tags = fields.TryGetProperty("System.Tags", out var tagsElement)
                        && tagsElement.ValueKind == JsonValueKind.String
                ? tagsElement.GetString()?.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
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
                ExternalUrl = $"{_options.BaseUrl?.TrimEnd('/')}/{project}/_workitems/edit/{itemId}",
                RepoKey = repoKey,
                Title = GetStringField(fields, "System.Title") ?? string.Empty,
                Description = GetStringField(fields, "System.Description"),
                AcceptanceCriteria = null, // Azure DevOps doesn't have a standard field; extensions may provide it
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
        CancellationToken cancellationToken)
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
            // 1. GET the current work item to read revision and tags
            var getResponse = await _http.GetAsync(
                $"{project}/_apis/wit/workitems/{workRef.ExternalId}?api-version=7.1&$expand=all",
                cancellationToken);

            if (!getResponse.IsSuccessStatusCode)
            {
                return new ClaimResult
                {
                    Success = false,
                    FailureReason = $"Failed to read work item {workRef.ExternalId}: " +
                                    $"HTTP {(int)getResponse.StatusCode} {getResponse.ReasonPhrase}.",
                };
            }

            var getJson = await getResponse.Content.ReadAsStringAsync(cancellationToken);
            using var getDoc = JsonDocument.Parse(getJson);

            var currentRev = getDoc.RootElement.TryGetProperty("rev", out var revEl)
                             && revEl.ValueKind == JsonValueKind.Number
                             && revEl.TryGetInt32(out var rev)
                ? rev
                : (int?)null;

            // 2. Check if already claimed — look for agent-active or agent-worker: tags
            var currentTags = string.Empty;
            if (getDoc.RootElement.TryGetProperty("fields", out var fields)
                && fields.TryGetProperty("System.Tags", out var tagsEl)
                && tagsEl.ValueKind == JsonValueKind.String)
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
                    FailureReason = $"Work item {workRef.ExternalId} is already claimed " +
                                    "(has 'agent-active' tag).",
                };
            }

            // Also check for any agent-worker: tag to detect re-claim attempts
            var workerTag = existingTags.FirstOrDefault(t =>
                t.StartsWith("agent-worker:", StringComparison.OrdinalIgnoreCase));
            if (workerTag is not null)
            {
                return new ClaimResult
                {
                    Success = false,
                    FailureReason = $"Work item {workRef.ExternalId} is already claimed " +
                                    $"by worker '{workerTag}'.",
                };
            }

            // 3. Build PATCH operations: add agent-active tag, agent-worker tag,
            //    and a claiming comment
            var newTags = string.IsNullOrWhiteSpace(currentTags)
                ? $"agent-active; agent-worker:{request.WorkerId}"
                : $"{currentTags.TrimEnd(';')}; agent-active; agent-worker:{request.WorkerId}";

            var claimedAt = request.ClaimedAt.ToString("yyyy-MM-dd HH:mm:ss UTC",
                System.Globalization.CultureInfo.InvariantCulture);

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
            var patchContent = new StringContent(patchBody, Encoding.UTF8,
                "application/json-patch+json");

            // 4. Use If-Match with the current revision for optimistic concurrency
            var patchRequest = new HttpRequestMessage(HttpMethod.Patch,
                $"{project}/_apis/wit/workitems/{workRef.ExternalId}?api-version=7.1")
            {
                Content = patchContent,
            };

            if (currentRev.HasValue)
            {
                patchRequest.Headers.TryAddWithoutValidation(
                    "If-Match", currentRev.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
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
                    WorkRef = workRef with { Revision = newRev?.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                    LeaseToken = $"lease_{workRef.ExternalId}_" +
                                 $"{newRev}_{DateTimeOffset.UtcNow:yyyyMMddHHmmss}",
                };
            }

            if (patchResponse.StatusCode == System.Net.HttpStatusCode.PreconditionFailed)
            {
                return new ClaimResult
                {
                    Success = false,
                    FailureReason = $"Work item {workRef.ExternalId} was modified " +
                                    "by another process (412 Precondition Failed).",
                };
            }

            var errorBody = await patchResponse.Content.ReadAsStringAsync(cancellationToken);
            return new ClaimResult
            {
                Success = false,
                FailureReason = $"Failed to claim work item {workRef.ExternalId}: " +
                                $"HTTP {(int)patchResponse.StatusCode} {patchResponse.ReasonPhrase}. " +
                                $"Details: {Truncate(errorBody, 200)}",
            };
        }
        catch (HttpRequestException ex)
        {
            return new ClaimResult
            {
                Success = false,
                FailureReason = $"Network error claiming work item {workRef.ExternalId}: {ex.Message}",
            };
        }
    }

    public async Task UpdateWorkItemStatusAsync(
        ExternalWorkRef workRef,
        ExternalWorkStatus status,
        CancellationToken cancellationToken)
    {
        var project = _options.Project;
        if (string.IsNullOrWhiteSpace(project) || string.IsNullOrWhiteSpace(workRef.ExternalId))
            return;

        // Build the PATCH body for work item update
        var patchOps = new List<object>();

        if (!string.IsNullOrWhiteSpace(status.Status))
        {
            patchOps.Add(new
            {
                op = "add",
                path = "/fields/System.State",
                value = status.Status,
            });
        }

        if (status.Tags is { Count: > 0 })
        {
            patchOps.Add(new
            {
                op = "add",
                path = "/fields/System.Tags",
                value = string.Join("; ", status.Tags),
            });
        }

        if (patchOps.Count == 0)
            return;

        var body = JsonSerializer.Serialize(patchOps, JsonOptions);
        var content = new StringContent(body, Encoding.UTF8, "application/json-patch+json");

        var request = new HttpRequestMessage(HttpMethod.Patch,
            $"{project}/_apis/wit/workitems/{workRef.ExternalId}?api-version=7.1")
        {
            Content = content,
        };

        // Use If-Match with revision for optimistic concurrency when available
        if (!string.IsNullOrWhiteSpace(workRef.Revision))
        {
            request.Headers.TryAddWithoutValidation("If-Match", workRef.Revision);
        }

        var response = await _http.SendAsync(request, cancellationToken);

        // 412 Precondition Failed is not thrown — it means someone else
        // modified the work item concurrently. We treat status projection as
        // best-effort: log and continue. The next poll cycle will pick up the
        // latest revision from QueryWorkItemsAsync.
        if (response.StatusCode == System.Net.HttpStatusCode.PreconditionFailed)
        {
            // Best-effort status projection: concurrent modification is not fatal.
            return;
        }

        response.EnsureSuccessStatusCode();
    }

    public async Task AddCommentAsync(
        ExternalWorkRef workRef,
        string comment,
        CancellationToken cancellationToken)
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

        var request = new HttpRequestMessage(HttpMethod.Patch,
            $"{project}/_apis/wit/workitems/{workRef.ExternalId}?api-version=7.1")
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

    // ─── WIQL builder ────────────────────────────────────

    private static string BuildWiql(string project, BoardsQueryParameters parameters)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"SELECT [System.Id] FROM WorkItems");
        sb.Append(CultureInfo.InvariantCulture, $" WHERE [System.TeamProject] = '{EscapeWiql(project)}'");

        if (parameters.States is { Count: > 0 })
        {
            var states = string.Join(", ", parameters.States.Select(s => $"'{EscapeWiql(s)}'"));
            sb.Append(CultureInfo.InvariantCulture, $" AND [System.State] IN ({states})");
        }

        if (parameters.Tags is { Count: > 0 })
        {
            foreach (var tag in parameters.Tags)
            {
                sb.Append(CultureInfo.InvariantCulture, $" AND [System.Tags] CONTAINS '{EscapeWiql(tag)}'");
            }
        }

        if (parameters.ExcludedTags is { Count: > 0 })
        {
            foreach (var tag in parameters.ExcludedTags)
            {
                sb.Append(CultureInfo.InvariantCulture, $" AND [System.Tags] NOT CONTAINS '{EscapeWiql(tag)}'");
            }
        }

        sb.Append(CultureInfo.InvariantCulture, $" ORDER BY [Microsoft.VSTS.Common.Priority] ASC, [System.CreatedDate] DESC");

        return sb.ToString();
    }

    private static string EscapeWiql(string value) =>
        value.Replace("'", "''");

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
            if (doc.RootElement.TryGetProperty("rev", out var revEl)
                && revEl.ValueKind == JsonValueKind.Number
                && revEl.TryGetInt32(out var rev))
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

    // ─── Field helpers ────────────────────────────────────

    private static string? GetStringField(JsonElement fields, string name)
    {
        return fields.TryGetProperty(name, out var element)
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
            if (element.ValueKind == JsonValueKind.String
                && int.TryParse(element.GetString(), out var strVal))
                return strVal;
        }

        return null;
    }

    private static string? GetAssignedToDisplayName(JsonElement fields)
    {
        if (fields.TryGetProperty("System.AssignedTo", out var assignedTo))
        {
            if (assignedTo.ValueKind == JsonValueKind.Object
                && assignedTo.TryGetProperty("displayName", out var displayName))
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
}

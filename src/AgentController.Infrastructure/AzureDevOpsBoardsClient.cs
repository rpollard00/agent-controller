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
            var fields = item.GetProperty("fields");
            var itemId = item.GetProperty("id").GetInt32();

            var tags = fields.TryGetProperty("System.Tags", out var tagsElement)
                        && tagsElement.ValueKind == JsonValueKind.String
                ? tagsElement.GetString()?.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    ?? Array.Empty<string>()
                : Array.Empty<string>();

            var candidate = new WorkCandidate
            {
                Id = $"wi_{itemId}",
                ExternalId = itemId.ToString(CultureInfo.InvariantCulture),
                ExternalUrl = fields.TryGetProperty("System.Url", out var urlEl)
                    ? urlEl.GetString()
                    : $"{_options.BaseUrl?.TrimEnd('/')}/{project}/_workitems/edit/{itemId}",
                RepoKey = string.Empty, // Resolved from work item fields/tags by the work source
                Title = GetStringField(fields, "System.Title") ?? string.Empty,
                Description = GetStringField(fields, "System.Description"),
                AcceptanceCriteria = null, // Azure DevOps doesn't have a standard field; extensions may provide it
                Priority = GetIntField(fields, "Microsoft.VSTS.Common.Priority"),
                Status = GetStringField(fields, "System.State"),
                Tags = tags,
                AssignedTo = GetAssignedToDisplayName(fields),
                Source = "AzureDevOpsBoards",
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
        // Claiming strategy: add an "agent-active" tag and optionally assign to service identity.
        // For now, this is a stub that returns success with a placeholder implementation.
        //
        // Full implementation in the subsequent work items will:
        // 1. Read the current work item revision
        // 2. Check if already claimed by another controller
        // 3. Patch the work item with tags/assignment using optimistic concurrency

        var project = _options.Project;
        if (string.IsNullOrWhiteSpace(project))
        {
            return new ClaimResult
            {
                Success = false,
                FailureReason = "Azure DevOps project is not configured.",
            };
        }

        return new ClaimResult
        {
            Success = true,
            WorkRef = workRef,
            LeaseToken = $"lease_{workRef.ExternalId}_{DateTimeOffset.UtcNow:yyyyMMddHHmmss}",
        };
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

        var response = await _http.PatchAsync(
            $"{project}/_apis/wit/workitems/{workRef.ExternalId}?api-version=7.1",
            content,
            cancellationToken);

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

        var response = await _http.PatchAsync(
            $"{project}/_apis/wit/workitems/{workRef.ExternalId}?api-version=7.1",
            content,
            cancellationToken);

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

using System.Text.Json;
using System.Text.Json.Serialization;
using AgentController.Api;
using AgentController.Api.Models;
using AgentController.Application;
using AgentController.Domain;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// Runtime events arrive over HTTP with severity as a lowercase string
// (e.g. "info", "warning", "error", "critical" — see docs/arch.md §10.2).
// Configure the minimal-API JSON binder to accept enums as case-insensitive
// strings (it still accepts numeric values, so existing typed clients are
// unaffected).
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(
        new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
});

// Register configuration options with validation-on-start
builder.Services.AddAgentControllerOptions(builder.Configuration);

// Register EF Core DbContext (scoped per-request/per-poll-cycle).
// Schema migrations are owned by the dedicated AgentController.Migrations
// console app; this registration never calls Migrate or EnsureCreated.
builder.Services.AddAgentControllerDbContext(builder.Configuration);

// Register EF Core-backed repository implementations for all application-layer
// persistence contracts. Requires AddAgentControllerDbContext to be called first.
builder.Services.AddAgentControllerRepositories();

// Register the controller run lifecycle service (scoped).
// Coordinates IAgentRunStore, ILifecycleEventStore, and IWorkItemStore
// for consistent run state transitions.
builder.Services.AddAgentControllerLifecycleService();

// ── Provider wiring ───────────────────────────────────────────
// Register no-op defaults first, then override with real providers
// based on configuration sections (workSource:provider, sourceControl:provider,
// environmentProvider:provider, runtime:provider).
// The last-registered implementation for each interface wins.
builder.Services.AddAgentControllerNoOpProviders();

var workSourceProvider = builder.Configuration.GetValue<string>("workSource:provider") ?? "LocalFake";
var sourceControlProvider = builder.Configuration.GetValue<string>("sourceControl:provider") ?? string.Empty;
var envProvider = builder.Configuration.GetValue<string>("environmentProvider:provider") ?? string.Empty;
var runtimeProvider = builder.Configuration.GetValue<string>("runtime:provider") ?? string.Empty;

// Work source (required — always override the no-op)
switch (workSourceProvider)
{
    case "LocalFile":
        builder.Services.AddAgentControllerLocalFileWorkSource();
        break;
    case "AzureDevOpsBoards":
        builder.Services.AddAgentControllerAzureDevOpsBoardsWorkSource();
        break;
    case "LocalFake":
    default:
        builder.Services.AddAgentControllerLocalFakeWorkSource();
        break;
}

// Source control provider (optional override)
if (sourceControlProvider.Equals("LocalGit", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddAgentControllerLocalGitSourceControl();
}

// Environment provider (optional override)
if (envProvider.Equals("LocalWorkspace", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddAgentControllerLocalWorkspaceEnvironment();
}

// Agent runtime (optional override)
if (runtimeProvider.Equals("MockPiMateria", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddAgentControllerMockPiMateriaRuntime();
}
else if (runtimeProvider.Equals("PiMateria", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddAgentControllerPiMateriaRuntime();
}
// ── End provider wiring ───────────────────────────────────────

// Register the background polling worker (disabled by default via agentController.workerEnabled).
// Kept in the same host as the API for the prototype; a future split can move this into a
// separate deployable without changing the domain or application contracts.
builder.Services.AddHostedService<PollingWorker>();

var app = builder.Build();

app.MapGet("/", () => "AgentController API");

app.MapGet(
    "/health",
    () => Results.Ok(new { Status = "Healthy", Timestamp = DateTimeOffset.UtcNow })
);

// --- ADO connection diagnostic endpoint ---

app.MapGet(
    "/azure-devops/diagnostic",
    async (
        IOptions<AgentController.Infrastructure.Options.WorkSourceOptions> workSourceOpts,
        IOptions<AgentController.Infrastructure.Options.AzureDevOpsBoardsOptions> boardsOpts,
        CancellationToken ct
    ) =>
    {
        var workSource = workSourceOpts.Value;
        var boards = boardsOpts.Value;

        var errors = new List<string>();

        // (1) Validate required configuration fields
        if (string.IsNullOrWhiteSpace(workSource.OrganizationUrl))
        {
            errors.Add("workSource:organizationUrl is not configured.");
        }
        if (string.IsNullOrWhiteSpace(workSource.Project))
        {
            errors.Add("workSource:project is not configured.");
        }

        // Resolve the PAT (may throw if ENV: reference is missing)
        string? resolvedPat = null;
        string? patError = null;
        try
        {
            resolvedPat = boards.ResolvePersonalAccessToken();
        }
        catch (InvalidOperationException ex)
        {
            patError = ex.Message;
            errors.Add($"PAT resolution failed: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(resolvedPat) && patError is null)
        {
            errors.Add("azureDevOps:personalAccessToken is not configured.");
        }

        // If config validation failed, return early without making API calls
        if (errors.Count > 0)
        {
            return Results.Ok(new
            {
                status = "ConfigurationError",
                organizationUrl = workSource.OrganizationUrl,
                project = workSource.Project,
                patConfigured = !string.IsNullOrWhiteSpace(resolvedPat),
                errors,
                timestamp = DateTimeOffset.UtcNow,
            });
        }

        // (2) Make a lightweight test API call to verify PAT and org URL
        string? apiError = null;
        int? statusCode = null;
        bool apiSuccess = false;
        var repositories = new List<object>();

        try
        {
            var orgUrl = workSource.OrganizationUrl!.TrimEnd('/');
            var project = workSource.Project!;

            using var http = new HttpClient();
            http.BaseAddress = new Uri(orgUrl + "/");

            // Basic auth with PAT (empty username, PAT as password)
            var authBytes = System.Text.Encoding.ASCII.GetBytes($":{resolvedPat}");
            http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue(
                    "Basic", Convert.ToBase64String(authBytes));

            // Lightweight test: GET project info (validates org URL + PAT + project)
            using var result = await http.GetAsync(
                $"_apis/projects/{project}?api-version=7.1",
                ct);

            statusCode = (int)result.StatusCode;

            if (result.IsSuccessStatusCode)
            {
                apiSuccess = true;

                // (2b) Enumerate repositories after successful connectivity test
                try
                {
                    using var reposResult = await http.GetAsync(
                        $"{project}/_apis/git/repositories?api-version=7.1",
                        ct);

                    if (reposResult.IsSuccessStatusCode)
                    {
                        var reposJson = await reposResult.Content.ReadAsStringAsync(ct);
                        using var reposDoc = System.Text.Json.JsonDocument.Parse(reposJson);
                        if (reposDoc.RootElement.TryGetProperty("value", out var val)
                            && val.ValueKind == System.Text.Json.JsonValueKind.Array)
                        {
                            foreach (var repo in val.EnumerateArray())
                            {
                                var id = repo.TryGetProperty("id", out var idEl) && idEl.ValueKind == System.Text.Json.JsonValueKind.String
                                    ? idEl.GetString()
                                    : null;
                                var name = repo.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == System.Text.Json.JsonValueKind.String
                                    ? nameEl.GetString()
                                    : null;
                                var defaultBranch = repo.TryGetProperty("defaultBranch", out var dbEl) && dbEl.ValueKind == System.Text.Json.JsonValueKind.String
                                    ? dbEl.GetString()
                                    : null;
                                var remoteUrl = repo.TryGetProperty("remoteUrl", out var ruEl) && ruEl.ValueKind == System.Text.Json.JsonValueKind.String
                                    ? ruEl.GetString()
                                    : null;

                                repositories.Add(new
                                {
                                    id,
                                    name,
                                    defaultBranch,
                                    remoteUrl,
                                });
                            }
                        }
                    }
                    else
                    {
                        var reposErrorBody = await reposResult.Content.ReadAsStringAsync(ct);
                        var reposError = $"Repository listing returned HTTP {(int)reposResult.StatusCode}: " +
                                         (reposErrorBody.Length > 200 ? reposErrorBody[..200] + "..." : reposErrorBody);
                        errors.Add(reposError);
                    }
                }
                catch (OperationCanceledException)
                {
                    errors.Add("Repository listing timed out or was cancelled.");
                }
                catch (System.Net.Http.HttpRequestException ex)
                {
                    errors.Add($"Repository listing failed: {ex.Message}");
                }
                catch (Exception ex)
                {
                    errors.Add($"Repository listing unexpected error: {ex.Message}");
                }
            }
            else
            {
                var errorBody = await result.Content.ReadAsStringAsync(ct);
                apiError = $"HTTP {(int)result.StatusCode} {result.ReasonPhrase}: " +
                           (errorBody.Length > 200 ? errorBody[..200] + "..." : errorBody);
                errors.Add(apiError);
            }
        }
        catch (OperationCanceledException)
        {
            apiError = "Request timed out or was cancelled.";
            errors.Add(apiError);
        }
        catch (System.Net.Http.HttpRequestException ex)
        {
            apiError = $"HTTP request failed: {ex.Message}";
            errors.Add(apiError);
        }
        catch (Exception ex)
        {
            apiError = $"Unexpected error: {ex.Message}";
            errors.Add(apiError);
        }

        // (3) Return diagnostic response (PAT is excluded for security)
        return Results.Ok(new
        {
            status = apiSuccess ? "Connected" : "ConnectionFailed",
            organizationUrl = workSource.OrganizationUrl,
            project = workSource.Project,
            patConfigured = true,
            httpStatusCode = statusCode,
            apiError,
            repositories,
            errors,
            timestamp = DateTimeOffset.UtcNow,
        });
    }
);

// --- Work item endpoints (Phase 1 local fake work items) ---

app.MapPost(
    "/work-items",
    async (CreateWorkItemRequest request, IWorkItemStore store, CancellationToken ct) =>
    {
        var created = await store.CreateAsync(request, ct);
        return Results.Created($"/work-items/{created.Id}", created);
    }
);

app.MapGet(
    "/work-items",
    async (
        string? status,
        string? repoKey,
        string? tags,
        int? maxResults,
        int? offset,
        IWorkItemStore store,
        CancellationToken ct
    ) =>
    {
        var tagList = tags?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var query = new WorkItemListQuery
        {
            Status = status,
            RepoKey = repoKey,
            Tags = tagList is { Length: > 0 } ? tagList : null,
            MaxResults = maxResults ?? 100,
            Offset = offset ?? 0,
        };

        var items = await store.ListAsync(query, ct);
        return Results.Ok(items);
    }
);

app.MapGet(
    "/work-items/{id}",
    async (string id, IWorkItemStore store, CancellationToken ct) =>
    {
        var item = await store.GetByIdAsync(id, ct);
        return item is null
            ? Results.NotFound(new { error = $"Work item '{id}' not found." })
            : Results.Ok(item);
    }
);

// --- Mock runtime event ingestion endpoint (Phase 1) ---

app.MapPost(
    "/runs/{runId}/events",
    async (
        string runId,
        RuntimeEventRequest request,
        IRunLifecycleService lifecycle,
        IAgentRunStore runStore,
        CancellationToken ct
    ) =>
    {
        // Validation: required fields
        if (string.IsNullOrWhiteSpace(request.EventType))
        {
            return Results.BadRequest(new
            {
                error = "Request body is missing required field 'eventType'."
            });
        }

        if (string.IsNullOrWhiteSpace(request.EventId))
        {
            return Results.BadRequest(new
            {
                error = "Request body is missing required field 'eventId'."
            });
        }

        // Validation: route runId / body runId consistency
        if (!string.IsNullOrWhiteSpace(request.RunId)
            && !request.RunId.Equals(runId, StringComparison.Ordinal))
        {
            return Results.BadRequest(new
            {
                error = $"RunId mismatch: route parameter '{runId}' does not match " +
                        $"request body 'runId' value '{request.RunId}'.",
                routeRunId = runId,
                bodyRunId = request.RunId,
            });
        }

        // Validation: event severity must be a defined enum value
        if (request.Severity.HasValue && !Enum.IsDefined(request.Severity.Value))
        {
            return Results.BadRequest(new
            {
                error = $"Unsupported severity value {(int)request.Severity.Value}. " +
                        $"Valid values: {string.Join(", ", Enum.GetNames<EventSeverity>())}."
            });
        }

        // Validation: occurredAt must not be in the far future (more than 5 minutes ahead)
        if (request.OccurredAt.HasValue
            && request.OccurredAt.Value > DateTimeOffset.UtcNow.AddMinutes(5))
        {
            return Results.BadRequest(new
            {
                error = $"Field 'occurredAt' is too far in the future. " +
                        $"Maximum allowed skew is 5 minutes.",
                provided = request.OccurredAt.Value.ToString("O"),
            });
        }

        var evt = new RuntimeEvent
        {
            EventId = request.EventId,
            RunId = runId,
            EventType = request.EventType,
            RuntimeRunId = request.RuntimeRunId,
            Sequence = request.Sequence,
            OccurredAt = request.OccurredAt ?? DateTimeOffset.UtcNow,
            Severity = request.Severity ?? EventSeverity.Info,
            Message = request.Message,
            Payload = request.Payload,
        };

        try
        {
            await lifecycle.IngestRuntimeEventAsync(evt, ct);
        }
        catch (InvalidOperationException ex)
        {
            // Distinguish idempotency conflicts from other validation errors.
            // Idempotency/duplicate → 409 Conflict.
            // Unsupported type, bad outcome, regression, missing field, terminal → 422 Unprocessable.
            var isDuplicate = ex.Message.Contains("already been processed", StringComparison.OrdinalIgnoreCase);

            return isDuplicate
                ? Results.Conflict(new
                {
                    error = ex.Message,
                    runId,
                    eventId = request.EventId,
                })
                : Results.UnprocessableEntity(new
                {
                    error = ex.Message,
                    runId,
                    eventId = request.EventId,
                });
        }

        // Fetch the updated run to return current state
        var updatedRun = await runStore.GetByIdAsync(runId, ct);
        return updatedRun is null
            ? Results.NotFound(new { error = $"Run '{runId}' not found after event ingestion." })
            : Results.Ok(new
            {
                runId = updatedRun.RunId,
                status = updatedRun.Status.ToString(),
                runtimeRunId = updatedRun.RuntimeRunId,
                lastHeartbeatAt = updatedRun.LastHeartbeatAt,
                finishedAt = updatedRun.FinishedAt,
                resultSummary = updatedRun.ResultSummary,
                error = updatedRun.Error,
                eventId = request.EventId,
                message = $"Runtime event '{request.EventType}' ingested successfully.",
            });
    }
);

// --- Run list and detail endpoints (Phase 1) ---

app.MapGet(
    "/runs",
    async (
        string? status,
        string? workItemId,
        int? maxResults,
        int? offset,
        IAgentRunStore runStore,
        IWorkItemStore workItemStore,
        CancellationToken ct
    ) =>
    {
        RunLifecycleState? statusFilter = null;
        if (!string.IsNullOrWhiteSpace(status)
            && Enum.TryParse<RunLifecycleState>(status, ignoreCase: true, out var parsed))
        {
            statusFilter = parsed;
        }

        var query = new RunListQuery
        {
            Status = statusFilter,
            WorkItemId = workItemId,
            MaxResults = maxResults ?? 100,
            Offset = offset ?? 0,
        };

        var runs = await runStore.ListAsync(query, ct);

        // Build summary items with optional work item titles
        var items = new List<RunListResponse>(runs.Count);
        foreach (var run in runs)
        {
            string? workItemTitle = null;
            string? repoKey = null;

            if (!string.IsNullOrWhiteSpace(run.WorkItemId))
            {
                var wi = await workItemStore.GetByIdAsync(run.WorkItemId, ct);
                if (wi is not null)
                {
                    workItemTitle = wi.Title;
                    repoKey = wi.RepoKey;
                }
            }

            items.Add(new RunListResponse
            {
                RunId = run.RunId,
                WorkItemId = run.WorkItemId,
                WorkItemTitle = workItemTitle,
                RepoKey = repoKey,
                Status = run.Status.ToString(),
                StartedAt = run.StartedAt,
                FinishedAt = run.FinishedAt,
                LastHeartbeatAt = run.LastHeartbeatAt,
                CreatedAt = run.CreatedAt,
            });
        }

        return Results.Ok(new RunListEnvelope
        {
            Runs = items,
            TotalCount = items.Count,
        });
    }
);

app.MapGet(
    "/runs/{runId}",
    async (
        string runId,
        IAgentRunStore runStore,
        IWorkItemStore workItemStore,
        ILifecycleEventStore lifecycleStore,
        IEnvironmentStore environmentStore,
        CancellationToken ct
    ) =>
    {
        var run = await runStore.GetByIdAsync(runId, ct);
        if (run is null)
        {
            return Results.NotFound(new { error = $"Run '{runId}' not found." });
        }

        // Fetch the associated work item
        WorkCandidate? workItem = null;
        if (!string.IsNullOrWhiteSpace(run.WorkItemId))
        {
            workItem = await workItemStore.GetByIdAsync(run.WorkItemId, ct);
        }

        // Fetch the environment if one exists
        EnvironmentHandle? environment = null;
        if (!string.IsNullOrWhiteSpace(run.EnvironmentId))
        {
            environment = await environmentStore.GetByIdAsync(run.EnvironmentId, ct);
        }

        // Fetch ordered lifecycle events
        var lifecycleEvents = await lifecycleStore.ListByRunIdAsync(runId, ct);

        var detail = new RunDetailResponse
        {
            RunId = run.RunId,
            WorkItemId = run.WorkItemId,
            WorkItem = workItem,
            EnvironmentId = run.EnvironmentId,
            RuntimeType = run.RuntimeType,
            RuntimeRunId = run.RuntimeRunId,
            Status = run.Status.ToString(),
            BranchName = run.BranchName,
            PullRequestUrl = run.PullRequestUrl,
            ResultSummary = run.ResultSummary,
            StartedAt = run.StartedAt,
            FinishedAt = run.FinishedAt,
            LastHeartbeatAt = run.LastHeartbeatAt,
            Error = run.Error,
            Environment = environment,
            LifecycleEvents = lifecycleEvents,
            CreatedAt = run.CreatedAt,
            UpdatedAt = run.UpdatedAt,
        };

        return Results.Ok(detail);
    }
);

app.Run();

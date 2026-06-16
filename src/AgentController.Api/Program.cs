using AgentController.Api;
using AgentController.Api.Models;
using AgentController.Application;
using AgentController.Domain;

var builder = WebApplication.CreateBuilder(args);

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

// Register deterministic no-op providers for DI seeding
// (source control, environment, runtime)
builder.Services.AddAgentControllerNoOpProviders();

// Override the no-op IWorkSource with a LocalFakeWorkSource backed by persisted WorkItems.
// Must be registered after AddAgentControllerNoOpProviders so the last-registered
// IWorkSource wins.
builder.Services.AddAgentControllerLocalFakeWorkSource();

// To use a declarative config/file-based work source instead of the API-seeded
// LocalFakeWorkSource, swap the registration above with:
//   builder.Services.AddAgentControllerLocalFileWorkSource();
// Then define work items in appsettings.json under "localWork": { "definitions": [...] },
// set "workSource:provider" to "LocalFile", and set "agentController:workerEnabled" to true.

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

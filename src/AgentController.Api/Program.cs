using AgentController.Api;
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

// Register deterministic no-op providers for DI seeding
// (source control, environment, runtime)
builder.Services.AddAgentControllerNoOpProviders();

// Override the no-op IWorkSource with a LocalFakeWorkSource backed by persisted WorkItems.
// Must be registered after AddAgentControllerNoOpProviders so the last-registered
// IWorkSource wins.
builder.Services.AddAgentControllerLocalFakeWorkSource();

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

app.Run();

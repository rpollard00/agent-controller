using System.Text.Json;
using System.Text.Json.Serialization;
using AgentController.Api;
using AgentController.Api.Endpoints;
using AgentController.Application;

var builder = WebApplication.CreateBuilder(args);

// Runtime events arrive over HTTP with severity as a lowercase string
// (e.g. "info", "warning", "error", "critical" — see docs/arch.md §10.2).
// Configure the minimal-API JSON binder to accept enums as case-insensitive
// strings (it still accepts numeric values, so existing typed clients are
// unaffected).
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(
        new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)
    );
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

// Register CQRS command/query handlers (scoped).
builder.Services.AddApplicationHandlers();

// ── Provider wiring ───────────────────────────────────────────
// Register no-op defaults first, then override with real providers
// based on configuration sections (workSource:provider, sourceControl:provider,
// environmentProvider:provider, runtime:provider).
// The last-registered implementation for each interface wins.
builder.Services.AddAgentControllerNoOpProviders();

var workSourceProvider =
    builder.Configuration.GetValue<string>("workSource:provider") ?? "LocalFake";
var sourceControlProvider =
    builder.Configuration.GetValue<string>("sourceControl:provider") ?? string.Empty;
var envProvider =
    builder.Configuration.GetValue<string>("environmentProvider:provider") ?? string.Empty;
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

// ── Feedback pipeline wiring ──────────────────────────────────
// Consolidated extension: selects IFeedbackSource provider via feedback:provider,
// registers the filter pipeline, and the feedback stores are already wired by
// AddAgentControllerRepositories (IReworkCycleStore, IReworkFeedbackStore).
builder.Services.AddAgentControllerFeedback(builder.Configuration);

// FeedbackPollingWorker lives in the Api project; register it here after
// the Infrastructure extension has wired the source and pipeline.
// The worker checks FeedbackOptions.Enabled at runtime.
builder.Services.AddHostedService<FeedbackPollingWorker>();

// ── End feedback pipeline wiring ──────────────────────────────

var app = builder.Build();

app.MapHealthEndpoints();
app.MapWorkItemEndpoints();
app.MapRunEndpoints();
app.MapRuntimeEventEndpoints();
app.MapAzureDevOpsDiagnosticEndpoints();
app.MapWebUiControllers();
app.MapWebUiHosting();

app.Run();

using AgentController.Api;

var builder = WebApplication.CreateBuilder(args);

// Register configuration options with validation-on-start
builder.Services.AddAgentControllerOptions(builder.Configuration);

// Register EF Core DbContext (scoped per-request/per-poll-cycle).
// Schema migrations are owned by the dedicated AgentController.Migrations
// console app; this registration never calls Migrate or EnsureCreated.
builder.Services.AddAgentControllerDbContext(builder.Configuration);

// Register deterministic no-op providers for DI seeding
builder.Services.AddAgentControllerNoOpProviders();

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

app.Run();

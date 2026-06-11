var builder = WebApplication.CreateBuilder(args);

// Register configuration options with validation-on-start
builder.Services.AddAgentControllerOptions(builder.Configuration);

// Register deterministic no-op providers for DI seeding
builder.Services.AddAgentControllerNoOpProviders();

var app = builder.Build();

app.MapGet("/", () => "AgentController API");

app.MapGet("/health", () => Results.Ok(new { Status = "Healthy", Timestamp = DateTimeOffset.UtcNow }));

app.Run();

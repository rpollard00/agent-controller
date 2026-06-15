using Projects; // Auto-generated project references

var builder = DistributedApplication.CreateBuilder(args);

// Migration runner — one-shot console app.
// In a subsequent change this will be configured to complete before
// the API starts (WaitForCompletion).
var migrations = builder.AddProject<AgentController_Migrations>("migrations");

// API — ASP.NET Core web host.
var api = builder.AddProject<AgentController_Api>("api");

builder.Build().Run();

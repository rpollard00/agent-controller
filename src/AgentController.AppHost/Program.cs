using Projects; // Auto-generated project references

// NixOS workaround:
// The Aspire CLI (`aspire-bin`) is a NativeAOT binary that cannot locate a
// new-enough ICU on NixOS, so the launcher wrapper starts it with
// DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1. That flag is inherited by every
// process this AppHost spawns. Unlike the CLI, those processes (dcp, the
// Aspire Dashboard, and our own projects) run on the shared .NET runtime,
// which finds ICU via its runpath and requires real globalization -- the
// Aspire Dashboard crashes on startup in invariant mode. Drop the flag from
// our own environment so child processes load ICU normally. This is a no-op
// on platforms where the flag was never set.
if (Environment.GetEnvironmentVariable("DOTNET_SYSTEM_GLOBALIZATION_INVARIANT") == "1")
{
    Environment.SetEnvironmentVariable("DOTNET_SYSTEM_GLOBALIZATION_INVARIANT", null);
}

var builder = DistributedApplication.CreateBuilder(args);

// Migration runner — one-shot console app.
var migrations = builder.AddProject<AgentController_Migrations>("migrations");

// API — ASP.NET Core web host.
var api = builder.AddProject<AgentController_Api>("api").WaitForCompletion(migrations);

// Web UI — Vite development server. The API reference supplies API_HTTP for
// Vite's /api proxy, and the wait keeps startup deterministic.
var webUi = builder.AddViteApp("webui", "../AgentController.WebUi")
    .WithHttpEndpoint(port: 5249)
    .WithReference(api)
    .WaitFor(api);

// In Aspire publish mode, the API owns the public production surface and receives
// the Vite build output in its wwwroot directory.
api.PublishWithContainerFiles(webUi, "wwwroot");

builder.Build().Run();

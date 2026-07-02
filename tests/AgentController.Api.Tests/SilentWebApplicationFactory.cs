using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;

namespace AgentController.Api.Tests;

/// <summary>
/// Shared <see cref="WebApplicationFactory{TEntryPoint}"/> base that silences
/// all console output on green test runs.
///
/// The API's appsettings.json wires up console logging at Information level
/// plus Debug-level categories for PollingWorker, RunLifecycleService,
/// IngestRuntimeEventCommandHandler, AzureDevOpsBoardsClient, and PiMateriaRuntime.
/// This base clears all providers and pins the default minimum level to Warning
/// so no log lines escape during test execution.
///
/// Deriving test classes should use this instead of bare
/// <c>new WebApplicationFactory&lt;Program&gt;()</c>.
/// </summary>
public class SilentWebApplicationFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureLogging(logging =>
        {
            // Remove every provider inherited from the app's host builder
            // (Console, Debug, etc. from appsettings.json).
            logging.ClearProviders();

            // Pin to Warning — no console provider is added back,
            // so even Warning-level messages produce no output.
            logging.SetMinimumLevel(LogLevel.Warning);
        });
    }
}

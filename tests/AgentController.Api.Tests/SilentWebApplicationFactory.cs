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
/// Also provides a test KEK file so the DB-backed secret provider can start
/// without requiring external KEK configuration.
///
/// Deriving test classes should use this instead of bare
/// <c>new WebApplicationFactory&lt;Program&gt;()</c>.
/// </summary>
public class SilentWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _testKekFilePath;

    public SilentWebApplicationFactory()
    {
        // Create a deterministic 32-byte KEK file for envelope encryption in tests.
        // Set the environment variable so RegisterDbNamedSecretProvider can find it.
        _testKekFilePath = Path.Combine(Path.GetTempPath(), $"agent-controller-test-kek-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(_testKekFilePath, new byte[32]);
        Environment.SetEnvironmentVariable("AGENT_CONTROLLER_SECRET_KEK_FILE_PATH", _testKekFilePath);
    }

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

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing)
        {
            Environment.SetEnvironmentVariable("AGENT_CONTROLLER_SECRET_KEK_FILE_PATH", null);

            if (File.Exists(_testKekFilePath))
            {
                try { File.Delete(_testKekFilePath); } catch { }
            }
        }
    }
}

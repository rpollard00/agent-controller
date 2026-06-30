using System.ComponentModel.DataAnnotations;

namespace AgentController.Infrastructure.Options;

/// <summary>
/// Agent runtime provider configuration.
/// Section: "runtime"
/// </summary>
public sealed class RuntimeOptions
{
    public const string SectionName = "runtime";

    /// <summary>
    /// Provider identifier (e.g. "PiMateria", "MockPiMateria", "NoOp").
    /// </summary>
    [Required]
    public string Provider { get; init; } = string.Empty;

    /// <summary>
    /// Path to the pi executable (when using PiMateria runtime).
    /// Defaults to <c>"pi"</c> (resolved via PATH).
    /// </summary>
    public string? PiExecutablePath { get; init; } = "pi";

    /// <summary>
    /// Base URL of this controller's HTTP API, e.g. <c>"http://localhost:5103"</c>.
    /// Required for the <c>PiMateria</c> runtime: it constructs
    /// <c>{ControllerBaseUrl}/runs/{runId}/events</c> and exposes it to the pi
    /// process as <c>CONTROLLER_EVENT_URL</c> so pi-materia can POST
    /// <c>runtime.*</c> events back to the controller's ingestion endpoint.
    /// No trailing slash.
    /// </summary>
    public string? ControllerBaseUrl { get; init; }

    /// <summary>
    /// Path to the PTY wrapper executable for launching the agent process.
    /// When set, the agent is launched inside this wrapper (e.g. <c>script</c> on Linux)
    /// so the agent's TUI can initialize headlessly via a pseudo-terminal.
    /// When null/empty/whitespace, the agent is launched directly without a wrapper
    /// (preserves behavior for non-Linux dev / future CRI sibling runtimes).
    /// Defaults to <c>"script"</c> (util-linux).
    /// </summary>
    public string? PtyWrapperPath { get; init; } = "script";

    /// <summary>
    /// Arguments to pass to the PTY wrapper before the agent command.
    /// Defaults to <c>"-qfc"</c> on Linux (quiet, flush, command-string mode).
    /// When <see cref="PtyWrapperPath"/> is unset this property is ignored.
    /// </summary>
    public string? PtyWrapperArgs { get; init; } = "-qfc";

    /// <summary>
    /// Target-to-source map for environment variables forwarded into the pi child process.
    ///
    /// Each entry maps a <c>target</c> environment variable name (set on the pi child)
    /// to a <c>source</c> environment variable name (read from the controller's own process environment).
    /// At runtime, each source variable is looked up via <c>Environment.GetEnvironmentVariable</c>;
    /// if the value is non-null and non-empty it is injected into the child, otherwise the entry
    /// is silently skipped (no exception is thrown).
    ///
    /// Default entries:
    /// <list type="bullet">
    ///   <item><term>AZURE_DEVOPS_EXT_PAT → AZURE_DEVOPS_PAT</term>
    ///     <description>Primary target for Azure DevOps CLI / <c>az</c> extension authentication.</description></item>
    ///   <item><term>AZURE_DEVOPS_PAT → AZURE_DEVOPS_PAT</term>
    ///     <description>Also forwarded for any pi tooling that reads the PAT directly by this name.</description></item>
    /// </list>
    ///
    /// This mirrors the <see cref="PtyWrapperPath"/> / <see cref="PtyWrapperArgs"/> config-seam pattern:
    /// operators can override the map in <c>appsettings</c> by providing a JSON object under
    /// <c>runtime:forwardEnvironmentVariables</c> with target names as keys and source names as values.
    /// </summary>
    public IDictionary<string, string> ForwardEnvironmentVariables { get; init; } = new Dictionary<string, string>
    {
        ["AZURE_DEVOPS_EXT_PAT"] = "AZURE_DEVOPS_PAT",
        ["AZURE_DEVOPS_PAT"] = "AZURE_DEVOPS_PAT",
        ["PI_MATERIA_EVENTING"] = "true",
        ["Pi_MATERIA_EVENTING_HEARTBEAT_MS"] = "30000",
        ["PI_MATERIA_EVENTING_PRESETS"] = "agent-controller"
    };
}

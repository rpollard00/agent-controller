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
}

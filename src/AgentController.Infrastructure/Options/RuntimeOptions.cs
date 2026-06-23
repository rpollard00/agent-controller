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
    /// Default materia loadout name to use for casts.
    /// Written into the controller-owned materia config (see <see cref="MateriaConfigPath"/>)
    /// as <c>activeLoadout</c>.
    /// </summary>
    public string? DefaultMateriaLoadout { get; init; }

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
    /// Optional path to a pre-written pi-materia config file. When set, the
    /// runtime passes it to pi via the <c>MATERIA_CONFIG</c> environment
    /// variable verbatim and does not write its own. When unset, the runtime
    /// writes a controller-owned config next to the run context that enables
    /// the <c>agent-controller</c> eventing preset and selects
    /// <see cref="DefaultMateriaLoadout"/>.
    /// </summary>
    public string? MateriaConfigPath { get; init; }

    /// <summary>
    /// Interval at which the runtime emits synthetic <c>runtime.heartbeat</c>
    /// events while the pi process is alive. This is a safety net only —
    /// pi-materia emits its own heartbeats via the webhook when eventing is
    /// enabled. Must be positive. Default: 30 seconds.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int HeartbeatIntervalSeconds { get; init; } = 30;

    /// <summary>
    /// When <c>true</c>, disables the synthetic heartbeat safety net. pi-materia
    /// is then solely responsible for keeping the run from going stale.
    /// </summary>
    public bool DisableSyntheticHeartbeat { get; init; }

    /// <summary>
    /// Maximum time to wait for the pi process to acknowledge the cast prompt
    /// (the RPC <c>prompt</c> response). Must be positive. Default: 60 seconds.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int PromptAcceptanceTimeoutSeconds { get; init; } = 60;

    /// <summary>
    /// Grace period between requesting pi shutdown (RPC <c>abort</c>) and
    /// force-killing the process tree. Must be positive. Default: 10 seconds.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int CancelGracePeriodSeconds { get; init; } = 10;
}

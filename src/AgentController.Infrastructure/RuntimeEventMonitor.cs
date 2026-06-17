using AgentController.Application;
using AgentController.Domain;
using AgentController.Infrastructure.Options;
using Microsoft.Extensions.Options;

namespace AgentController.Infrastructure;

/// <summary>
/// Reads runtime event artifacts from the local run workspace and projects them
/// into <see cref="MonitoringRuntimeEventSnapshot"/> for the monitoring/local
/// sync feed.
///
/// Resolves each run's workspace directory from
/// <see cref="AgentControllerOptions.RunRoot"/> (<c>{runRoot}/{runId}</c>) and
/// delegates the bounded, fault-tolerant read to
/// <see cref="RuntimeEventArtifactReader"/>. Registered as a singleton: it is
/// stateless and safe for concurrent consumption by the monitoring API.
/// </summary>
public sealed class RuntimeEventMonitor : IRuntimeEventMonitor
{
    private readonly IOptionsMonitor<AgentControllerOptions> _options;

    public RuntimeEventMonitor(IOptionsMonitor<AgentControllerOptions> options)
    {
        _options = options;
    }

    /// <inheritdoc />
    public Task<MonitoringRuntimeEventSnapshot> GetRuntimeEventsAsync(
        string runId,
        int? cap,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runId);

        var runRoot = ResolveRunRoot();
        var runDirectory = string.IsNullOrEmpty(runRoot)
            ? string.Empty
            : Path.Combine(runRoot, runId);

        var readOptions = new RuntimeEventArtifactReadOptions
        {
            RunId = runId,
            Cap = cap,
        };

        // The reader never throws for absent/malformed streams; an empty run
        // directory resolves to a missing file and yields an empty snapshot, so
        // monitoring never blocks local sync when no artifact exists yet.
        return RuntimeEventArtifactReader.ReadAsync(runDirectory, readOptions, cancellationToken);
    }

    /// <summary>
    /// Resolve the run root from configuration, expanding a leading tilde.
    /// Returns an empty string when no root is configured so the reader can
    /// short-circuit to an empty snapshot rather than throw.
    /// </summary>
    private string ResolveRunRoot()
    {
        var raw = _options.CurrentValue.RunRoot;
        return string.IsNullOrWhiteSpace(raw)
            ? string.Empty
            : LocalWorkspaceEnvironmentProvider.ExpandTilde(raw);
    }
}

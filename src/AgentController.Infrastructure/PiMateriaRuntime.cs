using System.Diagnostics;
using System.IO;
using AgentController.Application;
using AgentController.Application.Services;
using AgentController.Domain;
using AgentController.Infrastructure.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentController.Infrastructure;

/// <summary>
/// Minimal <see cref="IAgentRuntime"/> that launches <c>pi</c> inside an
/// ephemeral PTY-allocated shell (fire-and-forget). The invocation is:
/// <c>script -qfc 'pi "/materia loadout Elena" "/materia cast {task}"' /dev/null</c>.
///
/// <para>The <c>script</c> wrapper (util-linux) allocates a pseudo-terminal so
/// pi's TUI can initialize headlessly without deadlocking on missing TTY.</para>
///
/// <para>The controller injects only three environment variables:</para>
/// <list type="bullet">
///   <item><c>CONTROLLER_RUN_ID</c> — the run identifier.</item>
///   <item><c>CONTROLLER_EVENT_URL</c> — the webhook URL for pi-materia to POST
///       <c>runtime.*</c> events back to the controller.</item>
///   <item><c>CONTROLLER_CONTEXT_DIR</c> — path to the run context directory.</item>
/// </list>
///
/// <para>After spawn the runtime returns immediately. All observability comes
/// from the webhook-driven event ingestion path. On launch failure (executable
/// not found, etc.) a single failure event is synthesized for the run.</para>
///
/// <para>Stdin is held open for the lifetime of the session so the ephemeral
/// shell does not receive EOF and exit prematurely. The session lifecycle
/// work item owns stdin closure.</para>
///
/// <para><c>CancelAsync</c> is a no-op (detached process). <c>Dispose</c> is
/// trivial (no managed resources to release).</para>
///
/// <para>Registered as a singleton via
/// <see cref="AgentControllerServiceCollectionExtensions.AddAgentControllerPiMateriaRuntime"/>.</para>
/// </summary>
public sealed partial class PiMateriaRuntime : IAgentRuntime
{
    private const string DefaultCliLoadout = "Elena";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<RuntimeOptions> _runtimeOptions;
    private readonly ILogger<PiMateriaRuntime> _logger;

    public PiMateriaRuntime(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<RuntimeOptions> runtimeOptions,
        ILogger<PiMateriaRuntime> logger
    )
    {
        _scopeFactory = scopeFactory;
        _runtimeOptions = runtimeOptions;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<AgentRunHandle> StartAsync(
        AgentRunSpec spec,
        CancellationToken cancellationToken
    )
    {
        var options = _runtimeOptions.CurrentValue;

        // ── 1. Resolve controller event URL ───────────────────────────
        var baseUrl = options.ControllerBaseUrl;
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            Log.ControllerBaseUrlMissing(_logger, spec.RunId);
            _ = SynthesizeFailureAsync(
                spec.RunId,
                "PiMateria runtime is missing 'runtime:controllerBaseUrl'; cannot receive pi-materia webhook events.",
                CancellationToken.None
            );
            return Task.FromResult(DegenerateHandle(spec.RunId));
        }

        var eventUrl = $"{baseUrl.TrimEnd('/')}/runs/{spec.RunId}/events";
        Log.EventUrlResolved(_logger, spec.RunId, baseUrl, eventUrl);

        // ── 2. Prepare context directory and task text ────────────────
        var contextDir = Path.Combine(spec.EnvironmentHandle.RootPath, "context");
        Directory.CreateDirectory(contextDir);

        var repoPath = spec.RepoCheckout.LocalPath;
        var taskText = ReadCastTask(spec, contextDir);

        // ── 3. Build process start info (PTY-wrapped) ─────────────────
        // pi is a TUI app that deadlocks without a TTY. Wrap in `script -qfc ... /dev/null`
        // so a pseudo-terminal is allocated and the TUI initializes headlessly.
        var piExe = string.IsNullOrWhiteSpace(options.PiExecutablePath)
            ? "pi"
            : options.PiExecutablePath;

        // ── 4. Prepare log directory and file writers ─────────────────
        // ── 5. Start process (fire and forget) ────────────────────────
        try
        {
            // Validate the pi executable is resolvable before wrapping in script.
            // An absolute path that doesn't exist is a config error — fail fast.
            // A PATH-relative name that isn't found will also fail fast at launch.
            if (Path.IsPathRooted(piExe) && !File.Exists(piExe))
            {
                throw new FileNotFoundException(
                    $"Pi executable not found at configured path: {piExe}", piExe);
            }

            // Build the inner pi command string for the -c argument.
            var piCommand = $"{piExe} \"/materia loadout {DefaultCliLoadout}\" \"/materia cast {taskText}\"";

            var psi = new ProcessStartInfo
            {
                FileName = "script",
                WorkingDirectory = Directory.Exists(repoPath) ? repoPath : contextDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                // Redirect streams so the inherited pipe is always consumed (prevents child
                // from blocking on a full pipe buffer). Drained to log files, fire-and-forget.
                // These now capture PTY-normalized output from the script wrapper.
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                // Hold stdin open so the ephemeral shell does not receive EOF and exit.
                // The session lifecycle work item owns stdin closure.
                RedirectStandardInput = true,
            };

            // script -qfc '<pi command>' /dev/null
            //   -q = quiet, -f = flush after each write, -c = command string
            //   /dev/null = typescript file (we don't need the raw TTY dump)
            psi.ArgumentList.Add("-qfc");
            psi.ArgumentList.Add(piCommand);
            psi.ArgumentList.Add("/dev/null");

            // Controller ↔ pi-materia handoff environment.
            psi.Environment["CONTROLLER_RUN_ID"] = spec.RunId;
            psi.Environment["CONTROLLER_EVENT_URL"] = eventUrl;
            psi.Environment["CONTROLLER_CONTEXT_DIR"] = contextDir;

            var logsDir = Path.Combine(spec.EnvironmentHandle.RootPath, "logs");
            Directory.CreateDirectory(logsDir);

            var stdoutPath = Path.Combine(logsDir, "pi.stdout.log");
            var stderrPath = Path.Combine(logsDir, "pi.stderr.log");

            // Open file writers once per invocation. WriteThrough flushes on each write;
            // FileShare.Read allows external readers. Writers are fire-and-forget — they
            // live for the process lifetime and are GC'd when the process exits.
            var stdoutWriter = new StreamWriter(
                new FileStream(stdoutPath, FileMode.Create, FileAccess.Write, FileShare.Read,
                    bufferSize: 4096, FileOptions.WriteThrough));
            var stderrWriter = new StreamWriter(
                new FileStream(stderrPath, FileMode.Create, FileAccess.Write, FileShare.Read,
                    bufferSize: 4096, FileOptions.WriteThrough));

            var process = new Process { StartInfo = psi };

            // Wire up drain handlers — fire-and-forget background work on framework read threads.
            process.OutputDataReceived += (sender, e) =>
            {
                if (e.Data == null) return; // EndOfEvent
                try { stdoutWriter.WriteLine(e.Data); } catch { /* swallow — never kill the run */ }
            };
            process.ErrorDataReceived += (sender, e) =>
            {
                if (e.Data == null) return; // EndOfEvent
                try { stderrWriter.WriteLine(e.Data); } catch { /* swallow — never kill the run */ }
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // Do NOT close stdin — keep the ephemeral shell alive.
            // The session lifecycle work item owns stdin closure.
            // (StandardInput stream is left open intentionally.)

            var runtimeRunId = $"pi-{process.Id}";
            Log.RuntimeStarting(_logger, spec.RunId, runtimeRunId, piExe, repoPath);

            // Detach: do NOT retain the Process handle, do NOT WaitForExit.
            // The drain handlers run on the framework's internal read threads.
        }
        catch (Exception ex)
        {
            Log.ProcessStartFailed(_logger, spec.RunId, piExe, ex);
            _ = SynthesizeFailureAsync(
                spec.RunId,
                $"Failed to start pi process ('{piExe}'): {ex.Message}",
                CancellationToken.None
            );
            return Task.FromResult(DegenerateHandle(spec.RunId));
        }

        return Task.FromResult(new AgentRunHandle
        {
            RunId = spec.RunId,
            RuntimeRunId = $"pi-{spec.RunId}",
            Status = RunLifecycleState.AgentRunning,
            StartedAt = DateTimeOffset.UtcNow,
        });
    }

    /// <inheritdoc />
    public Task<AgentRuntimeStatus> GetStatusAsync(
        AgentRunHandle handle,
        CancellationToken cancellationToken
    )
    {
        // Fire-and-forget runtime: status is driven by webhook events.
        // Return the handle's persisted state.
        return Task.FromResult(new AgentRuntimeStatus
        {
            Status = handle.Status,
            RuntimeRunId = handle.RuntimeRunId,
            StartedAt = handle.StartedAt,
            LastHeartbeatAt = handle.LastHeartbeatAt,
            Error = handle.Error,
        });
    }

    /// <inheritdoc />
    public Task CancelAsync(AgentRunHandle handle, CancellationToken cancellationToken)
    {
        // Detached process — no RPC cancel available.
        // The process will complete on its own or be reclaimed by the OS.
        Log.RuntimeCancelled(_logger, handle.RunId);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "CodeQuality", "CA1822:Member does not access instance data", Justification = "Interface contract.")]
    public void Dispose()
    {
        // No managed resources to dispose (no process tracking, no monitors).
    }

    // ── Task reading helper ─────────────────────────────────────────

    /// <summary>
    /// Read the cast task text from the run context. Reads <c>work-item.md</c>
    /// and appends <c>acceptance-criteria.md</c> and <c>comments.md</c> when present.
    /// </summary>
    private static string ReadCastTask(AgentRunSpec spec, string contextDir)
    {
        var workItemPath = Path.Combine(contextDir, "work-item.md");
        if (File.Exists(workItemPath))
        {
            try
            {
                var parts = new List<string>();

                var workItemContent = File.ReadAllText(workItemPath).TrimEnd('\r', '\n');
                if (!string.IsNullOrEmpty(workItemContent))
                {
                    parts.Add(workItemContent);
                }

                var acPath = Path.Combine(contextDir, "acceptance-criteria.md");
                if (File.Exists(acPath))
                {
                    var acContent = File.ReadAllText(acPath).TrimEnd('\r', '\n');
                    if (!string.IsNullOrEmpty(acContent))
                    {
                        parts.Add("## Acceptance Criteria");
                        parts.Add(acContent);
                    }
                }

                var commentsPath = Path.Combine(contextDir, "comments.md");
                if (File.Exists(commentsPath))
                {
                    var commentsContent = File.ReadAllText(commentsPath).TrimEnd('\r', '\n');
                    if (!string.IsNullOrEmpty(commentsContent))
                    {
                        parts.Add("## Comments");
                        parts.Add(commentsContent);
                    }
                }

                if (parts.Count > 0)
                {
                    return string.Join("\n\n", parts);
                }
            }
            catch
            {
                // Fall through to fallback below.
            }
        }

        var external = !string.IsNullOrWhiteSpace(spec.WorkRef.ExternalId)
            ? spec.WorkRef.ExternalId
            : spec.RunId;
        return $"Complete work item {external}.";
    }

    // ── Event synthesis helpers ─────────────────────────────────────

    private async Task TryIngestAsync(string runId, RuntimeEvent evt, CancellationToken ct)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var lifecycle = scope.ServiceProvider.GetRequiredService<IRunLifecycleService>();
            await lifecycle.IngestRuntimeEventAsync(evt, ct);
        }
        catch (InvalidOperationException ex)
        {
            // The run may have already been transitioned (duplicate event id,
            // terminal state). Log and continue.
            Log.EventIngestRejected(_logger, runId, evt.EventId, ex.Message);
        }
        catch (Exception ex)
        {
            Log.EventIngestFailed(_logger, runId, evt.EventId, ex);
        }
    }

    private Task SynthesizeFailureAsync(string runId, string reason, CancellationToken ct)
    {
        Log.SynthesizingFailure(_logger, runId, reason);
        return TryIngestAsync(
            runId,
            new RuntimeEvent
            {
                EventId = $"pi-failed-{runId}-{Guid.NewGuid():N}",
                RunId = runId,
                EventType = RuntimeEventTypes.Failed,
                OccurredAt = DateTimeOffset.UtcNow,
                Severity = EventSeverity.Error,
                Message = reason,
                Payload = new Dictionary<string, object?>
                {
                    ["reason"] = "runtime_launch_failure",
                    ["summary"] = reason,
                    ["synthesized"] = true,
                },
            },
            ct
        );
    }

    private static AgentRunHandle DegenerateHandle(string runId) =>
        new()
        {
            RunId = runId,
            RuntimeRunId = $"pi-invalid-{runId}",
            Status = RunLifecycleState.AgentRunning,
            StartedAt = DateTimeOffset.UtcNow,
        };

    // ── Logger source-generated methods ─────────────────────────────

    private static partial class Log
    {
        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Event URL resolved for run '{RunId}' — controllerBaseUrl='{ControllerBaseUrl}', eventUrl='{EventUrl}'."
        )]
        public static partial void EventUrlResolved(
            ILogger logger,
            string runId,
            string controllerBaseUrl,
            string eventUrl
        );

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "PiMateria runtime starting for run '{RunId}' as '{RuntimeRunId}' (exe='{PiExe}', cwd='{RepoPath}')."
        )]
        public static partial void RuntimeStarting(
            ILogger logger,
            string runId,
            string runtimeRunId,
            string piExe,
            string repoPath
        );

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Runtime cancelled for run '{RunId}' (no-op: detached process)."
        )]
        public static partial void RuntimeCancelled(ILogger logger, string runId);

        [LoggerMessage(
            Level = LogLevel.Error,
            Message = "Failed to start pi process for run '{RunId}' ('{PiExe}')."
        )]
        public static partial void ProcessStartFailed(
            ILogger logger,
            string runId,
            string piExe,
            Exception ex
        );

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "PiMateria runtime is missing 'runtime:controllerBaseUrl'; cannot receive webhook events for run '{RunId}'."
        )]
        public static partial void ControllerBaseUrlMissing(ILogger logger, string runId);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Event '{EventId}' for run '{RunId}' was rejected: {Reason}."
        )]
        public static partial void EventIngestRejected(
            ILogger logger,
            string runId,
            string eventId,
            string reason
        );

        [LoggerMessage(
            Level = LogLevel.Error,
            Message = "Event '{EventId}' for run '{RunId}' could not be ingested."
        )]
        public static partial void EventIngestFailed(
            ILogger logger,
            string runId,
            string eventId,
            Exception ex
        );

        [LoggerMessage(
            Level = LogLevel.Error,
            Message = "Synthesizing a failure event for run '{RunId}': {Reason}."
        )]
        public static partial void SynthesizingFailure(ILogger logger, string runId, string reason);
    }
}

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentController.Application;
using AgentController.Application.Services;
using AgentController.Domain;
using AgentController.Infrastructure.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentController.Infrastructure;

/// <summary>
/// Real <see cref="IAgentRuntime"/> that launches <c>pi</c> as a child process in
/// RPC mode and drives a pi-materia cast. pi-materia reports <c>runtime.*</c>
/// events back to the controller over HTTP (POST to the controller's
/// <c>/runs/{runId}/events</c> endpoint); the adapter manages process lifecycle,
/// cancellation, synthetic heartbeats as a safety net, and synthesizes a final
/// event if pi exits without sending one via the webhook.
///
/// <para><b>Invocation model (verified).</b> pi is driven as
/// <c>pi --mode rpc --no-session</c> with the pi-materia extension loaded normally
/// (installed via <c>pi install</c>). The cast is started by sending the RPC
/// <c>prompt</c> command <c>/materia cast &lt;task&gt;</c>. RPC stdout carries the
/// <c>prompt</c> response and diagnostic events; the runtime lifecycle channel
/// (<c>runtime.accepted</c> … <c>runtime.completed</c>) is the HTTP webhook, not
/// stdout.</para>
///
/// <para><b>Eventing enablement.</b> The controller writes a controller-owned
/// pi-materia config next to the run context that enables the
/// <c>agent-controller</c> eventing preset and selects the configured loadout,
/// then points pi at it via the <c>MATERIA_CONFIG</c> environment variable. This
/// avoids mutating the cloned repository.</para>
///
/// <para>Registered as a singleton via
/// <see cref="AgentControllerServiceCollectionExtensions.AddAgentControllerPiMateriaRuntime"/>.
/// Uses <see cref="IServiceScopeFactory"/> internally to resolve the scoped
/// <see cref="IAgentRunStore"/> and <see cref="IRunLifecycleService"/> for state
/// queries, synthesized events, and synthetic heartbeats.</para>
/// </summary>
public sealed partial class PiMateriaRuntime : IAgentRuntime, IDisposable
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<RuntimeOptions> _runtimeOptions;
    private readonly ILogger<PiMateriaRuntime> _logger;

    private readonly ConcurrentDictionary<string, ActiveProcess> _activeProcesses = new();
    private readonly ConcurrentDictionary<string, Process> _processesByRunId = new();

    private static readonly JsonSerializerOptions RpcJsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // The pi RPC protocol uses lowercase field names
        // (e.g. {"type":"prompt","message":"..."}).
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

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
    public async Task<AgentRunHandle> StartAsync(
        AgentRunSpec spec,
        CancellationToken cancellationToken
    )
    {
        var options = _runtimeOptions.CurrentValue;

        // ── 1. Resolve controller event URL ───────────────────────────
        var baseUrl = options.ControllerBaseUrl;
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            // No base URL configured — we cannot receive webhook events.
            // Launch is still attempted (pi may run), but flag it and synthesize
            // a failure so the run does not hang forever.
            Log.ControllerBaseUrlMissing(_logger, spec.RunId);
            _ = SynthesizeFailureAsync(
                spec.RunId,
                "PiMateria runtime is missing 'runtime:controllerBaseUrl'; cannot receive pi-materia webhook events.",
                CancellationToken.None
            );
            return DegenerateHandle(spec.RunId);
        }

        var eventUrl = $"{baseUrl.TrimEnd('/')}/runs/{spec.RunId}/events";

        // ── 2. Prepare the controller-owned materia config + context env ──
        var contextDir = Path.Combine(spec.EnvironmentHandle.RootPath, "context");
        Directory.CreateDirectory(contextDir);
        var materiaConfigPath = ResolveMateriaConfigPath(options, contextDir);
        WriteControllerMateriaConfigIfNeeded(options, materiaConfigPath);

        var repoPath = spec.RepoCheckout.LocalPath;
        var taskText = ReadCastTask(spec, contextDir);

        // ── 3. Build process start info ───────────────────────────────
        var piExe = string.IsNullOrWhiteSpace(options.PiExecutablePath)
            ? "pi"
            : options.PiExecutablePath;

        var psi = new ProcessStartInfo
        {
            FileName = piExe,
            WorkingDirectory = Directory.Exists(repoPath) ? repoPath : contextDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        // RPC mode + no session persistence. The cast is driven via the
        // `prompt` RPC command after the process starts.
        psi.ArgumentList.Add("--mode");
        psi.ArgumentList.Add("rpc");
        psi.ArgumentList.Add("--no-session");

        // Controller ↔ pi-materia handoff environment (see arch §13b.5).
        psi.Environment["CONTROLLER_RUN_ID"] = spec.RunId;
        psi.Environment["CONTROLLER_EVENT_URL"] = eventUrl;
        psi.Environment["CONTROLLER_CONTEXT_DIR"] = contextDir;
        psi.Environment["MATERIA_CONFIG"] = materiaConfigPath;

        // ── 4. Start process ──────────────────────────────────────────
        Process process;
        try
        {
            process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            if (!process.Start())
            {
                throw new InvalidOperationException("Process.Start returned false.");
            }
        }
        catch (Exception ex)
        {
            Log.ProcessStartFailed(_logger, spec.RunId, piExe, ex);
            _ = SynthesizeFailureAsync(
                spec.RunId,
                $"Failed to start pi process ('{piExe}'): {ex.Message}",
                CancellationToken.None
            );
            return DegenerateHandle(spec.RunId);
        }

        var runtimeRunId = $"pi-{process.Id}";
        Log.RuntimeStarting(_logger, spec.RunId, runtimeRunId, piExe, repoPath);

        var cts = new CancellationTokenSource();
        var promptTcs = new TaskCompletionSource<bool>();
        var stdoutDone = new TaskCompletionSource<bool>();

        // Begin draining stdout (RPC responses + diagnostic events) and stderr.
        _ = ReadStdoutAsync(process, spec.RunId, promptTcs, stdoutDone);
        _ = ReadStderrAsync(process, spec.RunId);

        var active = new ActiveProcess(process, cts, promptTcs, stdoutDone);
        _activeProcesses[spec.RunId] = active;
        _processesByRunId[spec.RunId] = process;

        // ── 5. Background monitor: send prompt, await terminal/exit ───
        _ = MonitorAsync(spec.RunId, runtimeRunId, taskText, active, options, cancellationToken);

        // ── 6. Synthetic heartbeat safety net ─────────────────────────
        if (!options.DisableSyntheticHeartbeat)
        {
            _ = SyntheticHeartbeatAsync(
                spec.RunId,
                runtimeRunId,
                TimeSpan.FromSeconds(options.HeartbeatIntervalSeconds),
                cts.Token
            );
        }

        // ── 7. Return handle ──────────────────────────────────────────
        return new AgentRunHandle
        {
            RunId = spec.RunId,
            RuntimeRunId = runtimeRunId,
            Status = RunLifecycleState.AgentRunning,
            StartedAt = DateTimeOffset.UtcNow,
        };
    }

    /// <inheritdoc />
    public async Task<AgentRuntimeStatus> GetStatusAsync(
        AgentRunHandle handle,
        CancellationToken cancellationToken
    )
    {
        if (_processesByRunId.TryGetValue(handle.RunId, out var process) && !process.HasExited)
        {
            return new AgentRuntimeStatus
            {
                Status = RunLifecycleState.AgentRunning,
                RuntimeRunId = handle.RuntimeRunId,
                StartedAt = handle.StartedAt,
                LastHeartbeatAt = handle.LastHeartbeatAt,
            };
        }

        return new AgentRuntimeStatus
        {
            Status = handle.Status,
            RuntimeRunId = handle.RuntimeRunId,
            StartedAt = handle.StartedAt,
            LastHeartbeatAt = handle.LastHeartbeatAt,
            Error = handle.Error,
        };
    }

    /// <inheritdoc />
    public async Task CancelAsync(AgentRunHandle handle, CancellationToken cancellationToken)
    {
        if (!_activeProcesses.TryGetValue(handle.RunId, out var active))
        {
            Log.CancelNoActiveProcess(_logger, handle.RunId);
            return;
        }

        Log.RuntimeCancelled(_logger, handle.RunId);

        await ShutdownProcessAsync(handle.RunId, active, cancellationToken);

        // Record a runtime.cancelled event so the run transitions to Cancelled
        // even if pi did not acknowledge over the webhook.
        await TryIngestAsync(
            handle.RunId,
            new RuntimeEvent
            {
                EventId = $"pi-cancelled-{handle.RunId}-{Guid.NewGuid():N}",
                RunId = handle.RunId,
                EventType = RuntimeEventTypes.Cancelled,
                OccurredAt = DateTimeOffset.UtcNow,
                Severity = EventSeverity.Info,
                Message = "Runtime cancelled by controller request.",
            },
            CancellationToken.None
        );
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var (runId, active) in _activeProcesses)
        {
            try
            {
                active.Cts.Cancel();
                if (!active.Process.HasExited)
                {
                    active.Process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex)
            {
                Log.DisposeKillFailed(_logger, runId, ex);
            }
        }
        _activeProcesses.Clear();
        _processesByRunId.Clear();
    }

    // ── Background monitoring ───────────────────────────────────────

    /// <summary>
    /// Send the cast prompt, then wait for the run to reach a terminal state
    /// (driven by the webhook) or for the process to exit. On exit without a
    /// terminal event, synthesize one from the exit code.
    /// </summary>
    private async Task MonitorAsync(
        string runId,
        string runtimeRunId,
        string taskText,
        ActiveProcess active,
        RuntimeOptions options,
        CancellationToken ct
    )
    {
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(active.Cts.Token, ct);
        var token = linkedCts.Token;

        try
        {
            // Send the cast as an RPC prompt. pi-materia runs the loadout
            // autonomously and reports runtime.* events over the webhook.
            var prompt = new RpcPrompt
            {
                Id = $"cast-{runId}",
                Type = "prompt",
                Message = $"/materia cast {taskText}",
            };
            await WriteLineAsync(active.Process.StandardInput, prompt, token);

            // Wait for the prompt to be accepted (RPC response).
            var accepted = await AwaitPromptAcceptanceAsync(active.PromptAccepted, options, token);

            if (!accepted)
            {
                // pi never acknowledged the cast. Synthesize a failure so the
                // run does not hang in AwaitingResult until stale recovery.
                await SynthesizeFailureAsync(
                    runId,
                    "pi process did not acknowledge the cast prompt within the configured timeout.",
                    token
                );
                return;
            }

            Log.PromptAccepted(_logger, runId);

            // Wait for the webhook to drive the run to a terminal state, or for
            // the process to exit (whichever happens first).
            await WaitForTerminalOrExitAsync(runId, active, token);
        }
        catch (OperationCanceledException) when (active.Cts.IsCancellationRequested)
        {
            // Cancelled via CancelAsync — nothing more to do.
        }
        catch (Exception ex)
        {
            Log.MonitorFailed(_logger, runId, ex);
            await SynthesizeFailureAsync(
                runId,
                $"PiMateriaRuntime monitor failed: {ex.Message}",
                CancellationToken.None
            );
        }
        finally
        {
            linkedCts.Dispose();
        }
    }

    /// <summary>
    /// Poll until the run reaches a terminal state (via webhook) or the pi
    /// process exits. On exit without a terminal state, synthesize a final
    /// event from the exit code.
    /// </summary>
    private async Task WaitForTerminalOrExitAsync(
        string runId,
        ActiveProcess active,
        CancellationToken ct
    )
    {
        while (!ct.IsCancellationRequested)
        {
            // Process exited?
            if (active.Process.HasExited)
            {
                await HandleProcessExitAsync(runId, active.Process, ct);
                return;
            }

            // Run already terminal via webhook?
            if (await IsRunTerminalAsync(runId, ct))
            {
                Log.WebhookDroveTerminal(_logger, runId);
                await ShutdownProcessAsync(runId, active, ct);
                return;
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Handle pi process exit. If the run is still non-terminal (pi exited
    /// without sending a final webhook event), synthesize one from the exit
    /// code: exit 0 → no_changes_needed; non-zero → failed. If the run is
    /// already terminal, the webhook already delivered the final event — no
    /// synthesis needed.
    /// </summary>
    private async Task HandleProcessExitAsync(string runId, Process process, CancellationToken ct)
    {
        // Wait for stdout to finish draining so we captured any final response.
        if (_activeProcesses.TryGetValue(runId, out var active))
        {
            try
            {
                await active.StdoutDone.Task.WaitAsync(
                    TimeSpan.FromSeconds(2),
                    CancellationToken.None
                );
            }
            catch
            {
                // Best-effort drain.
            }
        }

        var exitCode = process.HasExited ? process.ExitCode : -1;
        var terminal = await IsRunTerminalAsync(runId, ct);

        if (terminal)
        {
            Log.ProcessExitedAfterTerminal(_logger, runId, exitCode);
            return;
        }

        Log.ProcessExitedWithoutTerminal(_logger, runId, exitCode);

        if (exitCode == 0)
        {
            await TryIngestAsync(
                runId,
                new RuntimeEvent
                {
                    EventId = $"pi-exit-completed-{runId}-{Guid.NewGuid():N}",
                    RunId = runId,
                    EventType = RuntimeEventTypes.Completed,
                    OccurredAt = DateTimeOffset.UtcNow,
                    Severity = EventSeverity.Info,
                    Message = "pi exited cleanly without a final event; synthesizing completion.",
                    Payload = new Dictionary<string, object?>
                    {
                        ["outcome"] = CompletionOutcomes.NoChangesNeeded,
                        ["summary"] =
                            "pi process exited cleanly without emitting a terminal runtime event.",
                        ["synthesized"] = true,
                        ["exitCode"] = 0,
                    },
                },
                CancellationToken.None
            );
        }
        else
        {
            await TryIngestAsync(
                runId,
                new RuntimeEvent
                {
                    EventId = $"pi-exit-failed-{runId}-{Guid.NewGuid():N}",
                    RunId = runId,
                    EventType = RuntimeEventTypes.Failed,
                    OccurredAt = DateTimeOffset.UtcNow,
                    Severity = EventSeverity.Error,
                    Message = $"pi exited with code {exitCode} without a final event.",
                    Payload = new Dictionary<string, object?>
                    {
                        ["reason"] = "process_exit_nonzero",
                        ["summary"] =
                            $"pi process exited with code {exitCode} without emitting a terminal runtime event.",
                        ["synthesized"] = true,
                        ["exitCode"] = exitCode,
                    },
                },
                CancellationToken.None
            );
        }
    }

    /// <summary>
    /// Emit a synthetic <c>runtime.heartbeat</c> on a fixed interval while the
    /// process is alive and the run is non-terminal. This is a safety net;
    /// pi-materia emits its own heartbeats via the webhook when eventing is
    /// enabled. Stops when the process exits, the run goes terminal, or
    /// cancellation is requested.
    /// </summary>
    private async Task SyntheticHeartbeatAsync(
        string runId,
        string runtimeRunId,
        TimeSpan interval,
        CancellationToken ct
    )
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(interval, ct);

                if (!_processesByRunId.TryGetValue(runId, out var process) || process.HasExited)
                {
                    return;
                }

                if (await IsRunTerminalAsync(runId, ct))
                {
                    return;
                }

                await TryIngestAsync(
                    runId,
                    new RuntimeEvent
                    {
                        EventId = $"pi-heartbeat-{runId}-{Guid.NewGuid():N}",
                        RunId = runId,
                        EventType = RuntimeEventTypes.Heartbeat,
                        OccurredAt = DateTimeOffset.UtcNow,
                        Severity = EventSeverity.Info,
                        Payload = new Dictionary<string, object?>
                        {
                            ["phase"] = "process-running",
                            ["synthetic"] = true,
                        },
                    },
                    ct
                );
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on cancel/shutdown.
        }
        catch (Exception ex)
        {
            Log.HeartbeatFailed(_logger, runId, ex);
        }
    }

    // ── Process shutdown ────────────────────────────────────────────

    /// <summary>
    /// Politely shut down pi: send the RPC <c>abort</c> command, wait the grace
    /// period, then force-kill the process tree if still alive. Removes the
    /// process from the active tracking maps.
    /// </summary>
    private async Task ShutdownProcessAsync(
        string runId,
        ActiveProcess active,
        CancellationToken ct
    )
    {
        active.Cts.Cancel();

        try
        {
            if (!active.Process.HasExited)
            {
                // Politely ask pi to abort over RPC.
                try
                {
                    await WriteLineAsync(
                        active.Process.StandardInput,
                        new RpcAbort(),
                        CancellationToken.None
                    );
                }
                catch
                {
                    // stdin may already be closed — fall through to kill.
                }

                try
                {
                    await active.Process.WaitForExitAsync(
                        CancellationTokenSource
                            .CreateLinkedTokenSource(
                                ct,
                                new CancellationTokenSource(
                                    TimeSpan.FromSeconds(
                                        _runtimeOptions.CurrentValue.CancelGracePeriodSeconds
                                    )
                                ).Token
                            )
                            .Token
                    );
                }
                catch (OperationCanceledException)
                {
                    // Grace period elapsed — force kill below.
                }

                if (!active.Process.HasExited)
                {
                    try
                    {
                        active.Process.Kill(entireProcessTree: true);
                    }
                    catch (Exception ex)
                    {
                        Log.ForceKillFailed(_logger, runId, ex);
                    }
                }
            }
        }
        finally
        {
            _activeProcesses.TryRemove(runId, out _);
            _processesByRunId.TryRemove(runId, out _);
        }
    }

    // ── stdout / stderr readers ─────────────────────────────────────

    private async Task ReadStdoutAsync(
        Process process,
        string runId,
        TaskCompletionSource<bool> promptTcs,
        TaskCompletionSource<bool> stdoutDone
    )
    {
        try
        {
            while (!process.HasExited)
            {
                string? line;
                try
                {
                    line = await process.StandardOutput.ReadLineAsync();
                }
                catch (Exception ex)
                {
                    Log.StdoutReadError(_logger, runId, ex);
                    break;
                }

                if (line is null)
                {
                    break;
                }

                Log.PiStdoutLine(_logger, runId, line);
                InterpretStdoutLine(line, runId, promptTcs);
            }
        }
        finally
        {
            stdoutDone.TrySetResult(true);
        }
    }

    /// <summary>
    /// Parse one RPC stdout line. We only act on the <c>prompt</c> response
    /// (to signal acceptance) and surface <c>extension_error</c> lines; we do
    /// not derive runtime lifecycle events from stdout — that is the webhook's
    /// responsibility.
    /// </summary>
    private void InterpretStdoutLine(
        string line,
        string runId,
        TaskCompletionSource<bool> promptTcs
    )
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        using var doc = JsonDocument.Parse(line);
        if (!doc.RootElement.TryGetProperty("type", out var typeEl))
        {
            return;
        }

        var type = typeEl.GetString() ?? "(null)";
        Log.PiStdoutParsed(_logger, runId, type);

        if (type == "response")
        {
            var command = doc.RootElement.TryGetProperty("command", out var cmdEl)
                ? cmdEl.GetString()
                : null;
            var success =
                doc.RootElement.TryGetProperty("success", out var okEl) && okEl.GetBoolean();

            if (command == "prompt")
            {
                promptTcs.TrySetResult(success);
                if (!success)
                {
                    var err = doc.RootElement.TryGetProperty("error", out var errEl)
                        ? errEl.GetString()
                        : "unknown";
                    Log.PromptRejected(_logger, runId, err ?? "unknown");
                }
            }

            return;
        }

        if (type == "extension_error")
        {
            var msg = doc.RootElement.TryGetProperty("error", out var errEl)
                ? errEl.GetString()
                : "(no message)";
            Log.PiExtensionError(_logger, runId, msg ?? "(no message)");
            return;
        }

        // Unrecognized type — warn with truncated raw line for diagnosis.
        var truncated = line.Length > 512 ? line[..512] + "..." : line;
        Log.PiStdoutUnknownType(_logger, runId, type, truncated);
    }

    private async Task ReadStderrAsync(Process process, string runId)
    {
        try
        {
            while (!process.HasExited)
            {
                string? line;
                try
                {
                    line = await process.StandardError.ReadLineAsync();
                }
                catch
                {
                    break;
                }

                if (line is null)
                {
                    break;
                }

                Log.PiStderr(_logger, runId, line);
            }
        }
        catch
        {
            // stderr capture is best-effort.
        }
    }

    // ── Config / context helpers ────────────────────────────────────

    private static string ResolveMateriaConfigPath(RuntimeOptions options, string contextDir)
    {
        if (!string.IsNullOrWhiteSpace(options.MateriaConfigPath))
        {
            return options.MateriaConfigPath;
        }

        return Path.Combine(contextDir, "materia-controller.json");
    }

    /// <summary>
    /// Write the controller-owned pi-materia config unless the operator
    /// supplied an explicit <see cref="RuntimeOptions.MateriaConfigPath"/>. The
    /// config enables the <c>agent-controller</c> eventing preset and selects
    /// the configured default loadout.
    /// </summary>
    private void WriteControllerMateriaConfigIfNeeded(
        RuntimeOptions options,
        string materiaConfigPath
    )
    {
        if (!string.IsNullOrWhiteSpace(options.MateriaConfigPath))
        {
            return;
        }

        try
        {
            var dir = Path.GetDirectoryName(materiaConfigPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var cfg = new MateriaControllerConfig
            {
                ActiveLoadout = string.IsNullOrWhiteSpace(options.DefaultMateriaLoadout)
                    ? null
                    : options.DefaultMateriaLoadout,
                Eventing = new MateriaEventingConfig
                {
                    Enabled = true,
                    Presets = ["agent-controller"],
                    HeartbeatIntervalMs = Math.Max(1, options.HeartbeatIntervalSeconds) * 1000,
                },
            };

            File.WriteAllText(materiaConfigPath, JsonSerializer.Serialize(cfg, RpcJsonOptions));
            Log.WroteMateriaConfig(_logger, materiaConfigPath);
        }
        catch (Exception ex)
        {
            Log.WriteMateriaConfigFailed(_logger, materiaConfigPath, ex);
        }
    }

    /// <summary>
    /// Read the cast task text. Prefers the title line from the controller's
    /// <c>work-item.md</c> context file; falls back to a generic task derived
    /// from the work reference.
    /// </summary>
    private static string ReadCastTask(AgentRunSpec spec, string contextDir)
    {
        var workItemPath = Path.Combine(contextDir, "work-item.md");
        if (File.Exists(workItemPath))
        {
            try
            {
                foreach (var raw in File.ReadLines(workItemPath))
                {
                    var line = raw.TrimStart();
                    if (line.StartsWith("# ", StringComparison.Ordinal))
                    {
                        return line[2..].Trim();
                    }
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

    // ── Lifecycle / store helpers ───────────────────────────────────

    private async Task<bool> IsRunTerminalAsync(string runId, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var runStore = scope.ServiceProvider.GetRequiredService<IAgentRunStore>();
        var run = await runStore.GetByIdAsync(runId, ct);
        if (run is null)
        {
            return false;
        }

        return run.Status
            is RunLifecycleState.Completed
                or RunLifecycleState.Failed
                or RunLifecycleState.Cancelled
                or RunLifecycleState.PrOpened
                or RunLifecycleState.BranchPushed
                or RunLifecycleState.NeedsHuman
                or RunLifecycleState.CleanedUp;
    }

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
                    ["reason"] = "runtime_launch_or_monitor_failure",
                    ["summary"] = reason,
                    ["synthesized"] = true,
                },
            },
            ct
        );
    }

    private static async Task<bool> AwaitPromptAcceptanceAsync(
        TaskCompletionSource<bool> promptAccepted,
        RuntimeOptions options,
        CancellationToken ct
    )
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(
                TimeSpan.FromSeconds(Math.Max(1, options.PromptAcceptanceTimeoutSeconds))
            );
            return await promptAccepted.Task.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static async Task WriteLineAsync(
        StreamWriter writer,
        object payload,
        CancellationToken ct
    )
    {
        var json = JsonSerializer.Serialize(payload, payload.GetType(), RpcJsonOptions);
        await writer.WriteLineAsync(json.AsMemory(), ct);
        await writer.FlushAsync(ct);
    }

    private static AgentRunHandle DegenerateHandle(string runId) =>
        new()
        {
            RunId = runId,
            RuntimeRunId = $"pi-invalid-{runId}",
            Status = RunLifecycleState.AgentRunning,
            StartedAt = DateTimeOffset.UtcNow,
        };

    // ── Internal types ──────────────────────────────────────────────

    [SuppressMessage(
        "Design",
        "CA1001:TypesThatOwnDisposableFieldsShouldBeDisposable",
        Justification = "Ownership is shared with the runtime; cleanup happens via Dispose/Cancel."
    )]
    private sealed class ActiveProcess
    {
        public ActiveProcess(
            Process process,
            CancellationTokenSource cts,
            TaskCompletionSource<bool> promptAccepted,
            TaskCompletionSource<bool> stdoutDone
        )
        {
            Process = process;
            Cts = cts;
            PromptAccepted = promptAccepted;
            StdoutDone = stdoutDone;
        }

        public Process Process { get; }
        public CancellationTokenSource Cts { get; }
        public TaskCompletionSource<bool> PromptAccepted { get; }
        public TaskCompletionSource<bool> StdoutDone { get; }
    }

    private sealed record RpcPrompt
    {
        public string? Id { get; init; }
        public string Type { get; init; } = "prompt";
        public string? Message { get; init; }
    }

    private sealed record RpcAbort
    {
        public string Type { get; init; } = "abort";
    }

    private sealed class MateriaControllerConfig
    {
        [JsonPropertyName("activeLoadout")]
        public string? ActiveLoadout { get; init; }

        [JsonPropertyName("eventing")]
        public MateriaEventingConfig? Eventing { get; init; }
    }

    private sealed class MateriaEventingConfig
    {
        [JsonPropertyName("enabled")]
        public bool Enabled { get; init; }

        [JsonPropertyName("presets")]
        public string[] Presets { get; init; } = [];

        [JsonPropertyName("heartbeatIntervalMs")]
        public int HeartbeatIntervalMs { get; init; }
    }

    // ── Logger source-generated methods ─────────────────────────────

    private static partial class Log
    {
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
            Message = "Cast prompt accepted by pi for run '{RunId}'."
        )]
        public static partial void PromptAccepted(ILogger logger, string runId);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Cast prompt rejected by pi for run '{RunId}': {Error}."
        )]
        public static partial void PromptRejected(ILogger logger, string runId, string error);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Run '{RunId}' reached a terminal state via webhook; shutting down pi process."
        )]
        public static partial void WebhookDroveTerminal(ILogger logger, string runId);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "pi process for run '{RunId}' exited with code {ExitCode} after the run was already terminal (no synthesis)."
        )]
        public static partial void ProcessExitedAfterTerminal(
            ILogger logger,
            string runId,
            int exitCode
        );

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "pi process for run '{RunId}' exited with code {ExitCode} without a terminal event; synthesizing one."
        )]
        public static partial void ProcessExitedWithoutTerminal(
            ILogger logger,
            string runId,
            int exitCode
        );

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Runtime cancelled for run '{RunId}'."
        )]
        public static partial void RuntimeCancelled(ILogger logger, string runId);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "CancelAsync: no active pi process tracked for run '{RunId}'."
        )]
        public static partial void CancelNoActiveProcess(ILogger logger, string runId);

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
            Level = LogLevel.Error,
            Message = "PiMateriaRuntime monitor failed for run '{RunId}'."
        )]
        public static partial void MonitorFailed(ILogger logger, string runId, Exception ex);

        [LoggerMessage(
            Level = LogLevel.Error,
            Message = "Synthesizing a failure event for run '{RunId}': {Reason}."
        )]
        public static partial void SynthesizingFailure(ILogger logger, string runId, string reason);

        [LoggerMessage(
            Level = LogLevel.Error,
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
            Level = LogLevel.Warning,
            Message = "Synthetic heartbeat failed for run '{RunId}'."
        )]
        public static partial void HeartbeatFailed(ILogger logger, string runId, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Force-kill failed for run '{RunId}'.")]
        public static partial void ForceKillFailed(ILogger logger, string runId, Exception ex);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Dispose force-kill failed for run '{RunId}'."
        )]
        public static partial void DisposeKillFailed(ILogger logger, string runId, Exception ex);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Wrote controller-owned materia config to '{Path}'."
        )]
        public static partial void WroteMateriaConfig(ILogger logger, string path);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Failed to write controller-owned materia config to '{Path}'."
        )]
        public static partial void WriteMateriaConfigFailed(
            ILogger logger,
            string path,
            Exception ex
        );

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "[pi stdout] ({RunId}) {Line}"
        )]
        public static partial void PiStdoutLine(ILogger logger, string runId, string line);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "[pi stdout] ({RunId}) parsed type='{Type}'"
        )]
        public static partial void PiStdoutParsed(ILogger logger, string runId, string type);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "[pi stdout] ({RunId}) unrecognized type='{Type}': {RawLine}"
        )]
        public static partial void PiStdoutUnknownType(
            ILogger logger,
            string runId,
            string type,
            string rawLine
        );

        [LoggerMessage(Level = LogLevel.Debug, Message = "[pi stderr] ({RunId}) {Line}")]
        public static partial void PiStderr(ILogger logger, string runId, string line);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "pi extension error for run '{RunId}': {Message}."
        )]
        public static partial void PiExtensionError(ILogger logger, string runId, string message);

        [LoggerMessage(Level = LogLevel.Debug, Message = "stdout read error for run '{RunId}'.")]
        public static partial void StdoutReadError(ILogger logger, string runId, Exception ex);
    }
}

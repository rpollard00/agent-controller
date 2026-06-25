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
/// <para><b>Stdout event contract.</b> The stdout JSONL events parsed by this class
/// are defined by <see cref="PiMateriaStdoutEventTypes"/> and
/// <see cref="PiMateriaStdoutEventContract"/> in the Domain layer. See
/// <c>docs/pi-materia-eventing-contract.md</c> for the full contract documentation
/// including the multiTurn fail-fast rule and unrecognized-type fail-closed behavior.</para>
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

        var active = new ActiveProcess(process, cts, promptTcs, stdoutDone);
        _activeProcesses[spec.RunId] = active;
        _processesByRunId[spec.RunId] = process;

        // Begin draining stdout (RPC responses + diagnostic events) and stderr.
        _ = ReadStdoutAsync(process, spec.RunId, promptTcs, stdoutDone, active);
        _ = ReadStderrAsync(process, spec.RunId);

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

            // RPC log: request being dispatched to the autonomous Elene runtime
            Log.RpcPromptSent(_logger, runId, prompt.Id, prompt.Message);

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
    ///
    /// Additionally monitors for:
    /// <list type="bullet">
    ///   <item>MultiTurn agent sockets — parsed from the <c>cast_start</c> stdout
    ///       event. Under agent-controller eventing the controller never sends
    ///       <c>/materia continue</c>, so a multiTurn agent socket can never
    ///       complete and is a guaranteed token sink. Fails the run immediately.
    ///   </item>
    ///   <item><c>agent_end</c> on stdout — signals the agent's single-turn cast
    ///       is complete. Initiates graceful shutdown so the run does not stall
    ///       waiting for a <c>/materia continue</c> that the agent-controller
    ///       never sends.</item>
    ///   <item>Unrecognized stdout event types — under agent-controller eventing
    ///       this indicates contract drift between pi-materia and the controller.
    ///       Fails the run to prevent silent token-sinking stalls.</item>
    /// </list>
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

            // MultiTurn agent sockets detected in cast_start event: the
            // agent-controller preset is active (autonomous run) and the
            // controller never sends /materia continue, so a multiTurn agent
            // socket can never complete and is a guaranteed token sink.
            // Fail the cast immediately — but ONLY under the agent-controller
            // preset. Under interactive/CLI eventing a human can drive continue
            // so multiTurn sockets are valid.
            if (active.HasMultiTurnAgentSockets &&
                string.Equals(active.EventingPreset, "agent-controller", StringComparison.Ordinal))
            {
                var socketList = string.Join(", ", active.MultiTurnSocketNames);
                await SynthesizeFailureAsync(
                    runId,
                    $"Cast aborted: multiTurn agent socket(s) detected under agent-controller eventing preset: {socketList}. " +
                    "The agent-controller sends a single /materia cast prompt and never sends /materia continue, " +
                    "so a multiTurn agent socket can never complete and is a guaranteed token sink. " +
                    "Use a single-turn agent materia (multiTurn: false or omitted) for autonomous runs.",
                    ct
                );
                await ShutdownProcessAsync(runId, active, ct);
                return;
            }

            // agent_end on stdout: the agent's single-turn cast is complete.
            // The agent-controller never sends /materia continue, so a multiTurn
            // agent socket would stall here forever. On agent_end, initiate
            // graceful shutdown — if the webhook hasn't driven the run terminal
            // yet, the process exit handler will synthesize a final event.
            if (active.AgentEndReceived)
            {
                Log.AgentEndInitiatingShutdown(_logger, runId);
                await ShutdownProcessAsync(runId, active, ct);
                return;
            }

            // Unrecognized stdout event type: contract drift between pi-materia
            // and the controller. Under agent-controller eventing (autonomous run),
            // fail the run to prevent silent token-sinking stalls. Under interactive/CLI
            // eventing a human is present to handle the situation, so warn-and-continue.
            if (active.HasUnrecognizedEventType &&
                string.Equals(active.EventingPreset, "agent-controller", StringComparison.Ordinal))
            {
                var unrecognizedType = active.UnrecognizedEventType ?? "(unknown)";
                var castId = active.CastId ?? "(unknown)";
                var materiaName = active.CurrentMateriaName ?? "(unknown)";
                await SynthesizeFailureAsync(
                    runId,
                    $"pi emitted an unrecognized stdout event type '{unrecognizedType}' " +
                    $"(castId={castId}, materia={materiaName}). " +
                    "This indicates contract drift between pi-materia and the agent-controller. " +
                    "The cast has been aborted to prevent a silent stall.",
                    new Dictionary<string, object?>
                    {
                        ["unrecognizedType"] = unrecognizedType,
                        ["castId"] = castId,
                        ["materiaName"] = materiaName,
                    },
                    ct
                );
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
    ///
    /// Defense-in-depth: if an unrecognized stdout event type was seen during
    /// the run, fail the run instead of synthesizing a completion. This catches
    /// contract drift even when the process exits before the monitor detects it.
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

            // Defense-in-depth: if an unrecognized stdout event type was seen
            // under agent-controller eventing, fail the run instead of synthesizing
            // a completion from exit code. Under interactive/CLI eventing a human
            // is present, so allow the normal exit-code synthesis path.
            if (active.HasUnrecognizedEventType &&
                string.Equals(active.EventingPreset, "agent-controller", StringComparison.Ordinal) &&
                !await IsRunTerminalAsync(runId, ct))
            {
                var unrecognizedType = active.UnrecognizedEventType ?? "(unknown)";
                var castId = active.CastId ?? "(unknown)";
                var materiaName = active.CurrentMateriaName ?? "(unknown)";
                Log.ContractDriftOnExit(_logger, runId, unrecognizedType);
                await SynthesizeFailureAsync(
                    runId,
                    $"pi emitted an unrecognized stdout event type '{unrecognizedType}' " +
                    $"(castId={castId}, materia={materiaName}) before exiting. " +
                    "This indicates contract drift between pi-materia and the agent-controller. " +
                    "The cast has been aborted to prevent a silent stall.",
                    new Dictionary<string, object?>
                    {
                        ["unrecognizedType"] = unrecognizedType,
                        ["castId"] = castId,
                        ["materiaName"] = materiaName,
                    },
                    ct
                );
                return;
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
        TaskCompletionSource<bool> stdoutDone,
        ActiveProcess active
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
                await InterpretStdoutLine(line, runId, promptTcs, active);
            }
        }
        finally
        {
            stdoutDone.TrySetResult(true);
        }
    }

    /// <summary>
    /// Parse one RPC stdout line. Recognized types are defined by
    /// <see cref="PiMateriaStdoutEventTypes.AllRecognizedTypes">:
    /// <list type="bullet">
    ///   <item><c>response</c> — RPC command response (signals prompt acceptance).</item>
    ///   <item><c>extension_error</c> — pi-materia extension error (logged as warning).</item>
    ///   <item><c>cast_start</c> — pi-materia cast initialization event containing
    ///       resolved socket metadata. Under agent-controller eventing, the runtime
    ///       inspects this for multiTurn agent sockets and fails the cast fast if
    ///       any are found (the controller never sends <c>/materia continue</c>).
    ///       The full socket metadata is persisted as a lifecycle artifact for
    ///       diagnosability.</item>
    ///   <item><c>cast_end</c> — pi-materia cast completion event (informational).</item>
    ///   <item><c>agent_end</c> — pi-materia agent socket completed its single turn.
    ///       Under agent-controller eventing this signals the cast is done; the
    ///       monitor will initiate graceful shutdown so the run does not stall
    ///       waiting for a <c>/materia continue</c> that the controller never sends.</item>
    ///   <item><c>materia_start</c> — pi-materia materia socket started (informational).</item>
    ///   <item><c>materia_end</c> — pi-materia materia socket completed (informational).</item>
    /// </list>
    /// Unrecognized types are tracked for fail-closed handling under agent-controller
    /// eventing (contract drift defense). We do not derive runtime lifecycle events
    /// from stdout — that is the webhook's responsibility.
    /// </summary>
    private async Task InterpretStdoutLine(
        string line,
        string runId,
        TaskCompletionSource<bool> promptTcs,
        ActiveProcess? active = null,
        CancellationToken ct = default
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

        if (type == AgentController.Domain.PiMateriaStdoutEventTypes.Response)
        {
            var command = doc.RootElement.TryGetProperty("command", out var cmdEl)
                ? cmdEl.GetString()
                : null;
            var success =
                doc.RootElement.TryGetProperty("success", out var okEl) && okEl.GetBoolean();

            if (command == "prompt")
            {
                // RPC log: response received from the autonomous Elene runtime
                Log.RpcResponseReceived(_logger, runId, command ?? "(null)", success);

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

        if (type == AgentController.Domain.PiMateriaStdoutEventTypes.ExtensionError)
        {
            var msg = doc.RootElement.TryGetProperty("error", out var errEl)
                ? errEl.GetString()
                : "(no message)";
            Log.PiExtensionError(_logger, runId, msg ?? "(no message)");
            return;
        }

        if (type == AgentController.Domain.PiMateriaStdoutEventTypes.AgentEnd)
        {
            // pi-materia agent socket completed its turn. Under agent-controller
            // eventing the controller never sends /materia continue, so this
            // signals the cast is done. Signal the monitor to shut down.
            Log.PiAgentEndReceived(_logger, runId);
            active?.SetAgentEndReceived();
            return;
        }

        if (type == AgentController.Domain.PiMateriaStdoutEventTypes.CastStart)
        {
            // pi-materia cast initialization event containing resolved socket
            // metadata. Under agent-controller eventing, inspect for multiTurn
            // agent sockets and fail the cast fast if any are found.
            // Also persist the full socket metadata as a lifecycle artifact.

            // Extract the eventing preset from cast_start.
            var eventingPreset = doc.RootElement.TryGetProperty("eventing", out var eventingEl) &&
                                 eventingEl.TryGetProperty("preset", out var presetEl)
                ? presetEl.GetString()
                : null;

            // Extract all socket metadata for artifact enrichment.
            var socketMetadata = ExtractAllSocketMetadata(doc);
            if (socketMetadata.Count > 0 && active is not null)
            {
                active.SetSocketMetadata(socketMetadata);
            }

            var multiTurnSockets = ExtractMultiTurnAgentSockets(doc);
            if (multiTurnSockets.Count > 0 && active is not null)
            {
                active.SetMultiTurnAgentSockets(multiTurnSockets, eventingPreset);
                Log.MultiTurnAgentSocketsDetected(
                    _logger,
                    runId,
                    string.Join(", ", multiTurnSockets)
                );
            }
            else if (eventingPreset is not null && active is not null)
            {
                // Even without multiTurn sockets, store the preset for correctness.
                active.EventingPreset = eventingPreset;
            }

            // Extract and store the castId for diagnostic enrichment of later
            // unrecognized-type warnings.
            var castStartId = doc.RootElement.TryGetProperty("castId", out var castIdEl)
                ? castIdEl.GetString()
                : null;
            if (castStartId is not null && active is not null)
            {
                active.CastId = castStartId;
            }

            // Persist the cast_start event as a lifecycle artifact with enriched
            // socket metadata (names + multiTurn flags) for diagnosability.
            await IngestCastStartArtifactAsync(runId, doc, socketMetadata, ct);
            return;
        }

        if (type == AgentController.Domain.PiMateriaStdoutEventTypes.CastEnd)
        {
            // pi-materia cast completion event (informational).
            // The runtime does not derive lifecycle state from this; the webhook
            // (runtime.completed) is authoritative.
            Log.PiStdoutIntermediate(_logger, runId, type);
            return;
        }

        if (type == AgentController.Domain.PiMateriaStdoutEventTypes.MateriaStart)
        {
            // pi-materia materia socket started execution (informational).
            // Track the current materia name for diagnostic enrichment.
            var startMateriaName = doc.RootElement.TryGetProperty("materiaName", out var mnEl)
                ? mnEl.GetString()
                : null;
            if (startMateriaName is not null && active is not null)
            {
                active.CurrentMateriaName = startMateriaName;
            }
            Log.PiStdoutIntermediate(_logger, runId, type);
            return;
        }

        if (type == AgentController.Domain.PiMateriaStdoutEventTypes.MateriaEnd)
        {
            // pi-materia materia socket completed execution (informational).
            Log.PiStdoutIntermediate(_logger, runId, type);
            return;
        }

        // Enrich the unrecognized-type warning with cast id and resolved materia
        // name so future contract drift is immediately traceable.
        var castId = active?.CastId ?? "(unknown)";
        var materiaName = active?.CurrentMateriaName ?? "(unknown)";
        var truncated = line.Length > 512 ? line[..512] + "..." : line;
        Log.PiStdoutUnknownType(_logger, runId, type, castId, materiaName, truncated);
        active?.SetUnrecognizedType(type);
    }

    /// <summary>
    /// Extract names of agent sockets with <c>multiTurn: true</c> from a
    /// <c>cast_start</c> stdout event. The expected JSON shape:
    /// <code>{ "type": "cast_start", "sockets": [ { "socketName": "Socket-4", "type": "agent", "multiTurn": true }, ... ] }</code>
    /// Returns an empty list if the sockets array is missing, malformed, or
    /// contains no multiTurn agent sockets.
    /// </summary>
    private static List<string> ExtractMultiTurnAgentSockets(JsonDocument doc)
    {
        var result = new List<string>();

        if (!doc.RootElement.TryGetProperty("sockets", out var socketsEl))
        {
            return result;
        }

        if (socketsEl.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var socket in socketsEl.EnumerateArray())
        {
            // Only agent-type sockets matter for the multiTurn fail-fast check.
            if (socket.TryGetProperty("type", out var socketTypeEl)
                && socketTypeEl.GetString() == "agent")
            {
                if (socket.TryGetProperty("multiTurn", out var multiTurnEl)
                    && multiTurnEl.GetBoolean())
                {
                    var name = socket.TryGetProperty("socketName", out var nameEl)
                        ? nameEl.GetString()
                        : "(unnamed)";
                    if (!string.IsNullOrEmpty(name))
                    {
                        result.Add(name);
                    }
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Extract all socket metadata from a <c>cast_start</c> stdout event.
    /// Returns a list of <see cref="ActiveProcess.SocketMetadata"/> for every
    /// socket in the pipeline, regardless of type or multiTurn flag.
    /// </summary>
    private static List<ActiveProcess.SocketMetadataEntry> ExtractAllSocketMetadata(JsonDocument doc)
    {
        var result = new List<ActiveProcess.SocketMetadataEntry>();

        if (!doc.RootElement.TryGetProperty("sockets", out var socketsEl))
        {
            return result;
        }

        if (socketsEl.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var socket in socketsEl.EnumerateArray())
        {
            result.Add(new ActiveProcess.SocketMetadataEntry
            {
                SocketName = socket.TryGetProperty("socketName", out var nameEl)
                    ? nameEl.GetString()
                    : null,
                Type = socket.TryGetProperty("type", out var typeEl)
                    ? typeEl.GetString()
                    : null,
                MateriaName = socket.TryGetProperty("materiaName", out var materiaEl)
                    ? materiaEl.GetString()
                    : null,
                MultiTurn = socket.TryGetProperty("multiTurn", out var mtEl)
                    && mtEl.GetBoolean(),
            });
        }

        return result;
    }

    /// <summary>
    /// Ingest the <c>cast_start</c> event as a lifecycle artifact with enriched
    /// socket metadata. This persists the resolved per-socket materia names
    /// along with their multiTurn flags so future misconfigurations are
    /// diagnosable at a glance from the run artifact log.
    /// </summary>
    private async Task IngestCastStartArtifactAsync(
        string runId,
        JsonDocument castStartDoc,
        List<ActiveProcess.SocketMetadataEntry> socketMetadata,
        CancellationToken ct
    )
    {
        var socketArray = new List<Dictionary<string, object?>>();
        foreach (var sm in socketMetadata)
        {
            socketArray.Add(new Dictionary<string, object?>
            {
                ["socketName"] = sm.SocketName,
                ["type"] = sm.Type,
                ["materiaName"] = sm.MateriaName,
                ["multiTurn"] = sm.MultiTurn,
            });
        }

        var castId = castStartDoc.RootElement.TryGetProperty("castId", out var castIdEl)
            ? castIdEl.GetString()
            : null;

        await TryIngestAsync(
            runId,
            new RuntimeEvent
            {
                EventId = $"pi-cast-start-{runId}-{Guid.NewGuid():N}",
                RunId = runId,
                // Use runtime.status as the event type — it is an informational
                // no-op in the lifecycle service (does not drive state transitions)
                // but still persists the event and payload as a run artifact.
                EventType = RuntimeEventTypes.Status,
                OccurredAt = DateTimeOffset.UtcNow,
                Severity = EventSeverity.Info,
                Message = $"Cast started{(!string.IsNullOrEmpty(castId) ? $" (castId={castId})" : "")} with {socketMetadata.Count} socket(s).",
                Payload = new Dictionary<string, object?>
                {
                    ["castId"] = castId,
                    ["sockets"] = socketArray,
                    ["socketCount"] = socketMetadata.Count,
                    ["hasMultiTurnAgentSockets"] = socketMetadata.Any(
                        s => s.Type == "agent" && s.MultiTurn
                    ),
                },
            },
            ct
        );
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
    /// Read the cast task text. Reads the full contents of
    /// <c>work-item.md</c> and appends <c>acceptance-criteria.md</c> and
    /// <c>comments.md</c> when present. Falls back to a generic task derived
    /// from the work reference when <c>work-item.md</c> is absent.
    /// </summary>
    private static string ReadCastTask(AgentRunSpec spec, string contextDir)
    {
        var workItemPath = Path.Combine(contextDir, "work-item.md");
        if (File.Exists(workItemPath))
        {
            try
            {
                var parts = new List<string>();

                // Full work-item body (entire markdown, not just the title line).
                var workItemContent = File.ReadAllText(workItemPath).TrimEnd('\r', '\n');
                if (!string.IsNullOrEmpty(workItemContent))
                {
                    parts.Add(workItemContent);
                }

                // Append acceptance criteria if present.
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

                // Append comments if present.
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

    /// <summary>
    /// Synthesize a failure event with additional diagnostic payload fields.
    /// Used for contract-drift failures that include cast id, materia name, etc.
    /// </summary>
    private Task SynthesizeFailureAsync(
        string runId,
        string reason,
        Dictionary<string, object?> extraPayload,
        CancellationToken ct
    )
    {
        Log.SynthesizingFailure(_logger, runId, reason);
        var payload = new Dictionary<string, object?>
        {
            ["reason"] = "runtime_launch_or_monitor_failure",
            ["summary"] = reason,
            ["synthesized"] = true,
        };
        foreach (var kvp in extraPayload)
        {
            payload[kvp.Key] = kvp.Value;
        }
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
                Payload = payload,
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

        /// <summary>
        /// Signaled when pi emits <c>agent_end</c> on stdout, indicating the
        /// agent's single-turn cast is complete. The monitor uses this to
        /// initiate graceful shutdown so the run does not stall waiting for
        /// a <c>/materia continue</c> that the agent-controller never sends.
        /// </summary>
        public bool AgentEndReceived { get; set; }

        /// <summary>
        /// Set when an unrecognized stdout event type is encountered under
        /// agent-controller eventing. The monitor checks this flag and
        /// fails the run to prevent silent stalls from contract drift.
        /// </summary>
        public bool HasUnrecognizedEventType { get; set; }

        /// <summary>The first unrecognized event type string, for diagnostics.</summary>
        public string? UnrecognizedEventType { get; set; }

        /// <summary>
        /// Eventing preset extracted from the <c>cast_start</c> stdout event
        /// (e.g. "agent-controller", "interactive"). Used to determine whether
        /// the multiTurn-agent-socket fail-fast guard applies.
        /// </summary>
        public string? EventingPreset { get; set; }

        /// <summary>
        /// Set when a <c>cast_start</c> stdout event reveals one or more agent
        /// sockets with <c>multiTurn: true</c> in the resolved pipeline. Under
        /// agent-controller eventing this is a fatal misconfiguration: the
        /// controller never sends <c>/materia continue</c> so a multiTurn agent
        /// socket can never complete and is a guaranteed token sink.
        /// </summary>
        public bool HasMultiTurnAgentSockets { get; set; }

        /// <summary>
        /// Names of agent sockets that have <c>multiTurn: true</c>, captured from
        /// the <c>cast_start</c> stdout event. Used in failure diagnostics to name
        /// the offending socket(s).
        /// </summary>
        public string[] MultiTurnSocketNames { get; set; } = Array.Empty<string>();

        /// <summary>
        /// Full socket metadata captured from the <c>cast_start</c> stdout event.
        /// Persisted as a run artifact for diagnosability.
        /// </summary>
        public SocketMetadataEntry[] SocketMetadata { get; set; } = Array.Empty<SocketMetadataEntry>();

        /// <summary>
        /// Cast ID extracted from the <c>cast_start</c> stdout event.
        /// Used to enrich unrecognized-type warnings so future contract drift
        /// is immediately traceable to the specific cast.
        /// </summary>
        public string? CastId { get; set; }

        /// <summary>
        /// Current materia name extracted from <c>materia_start</c> stdout events.
        /// Used to enrich unrecognized-type warnings so future contract drift
        /// is immediately traceable to the specific materia.
        /// </summary>
        public string? CurrentMateriaName { get; set; }

        public void SetAgentEndReceived() => AgentEndReceived = true;

        public void SetUnrecognizedType(string type)
        {
            HasUnrecognizedEventType = true;
            if (UnrecognizedEventType is null)
            {
                UnrecognizedEventType = type;
            }
        }

        public void SetMultiTurnAgentSockets(IEnumerable<string> socketNames, string? eventingPreset)
        {
            HasMultiTurnAgentSockets = true;
            MultiTurnSocketNames = socketNames.ToArray();
            EventingPreset = eventingPreset;
        }

        public void SetSocketMetadata(IEnumerable<SocketMetadataEntry> metadata)
        {
            SocketMetadata = metadata.ToArray();
        }

        /// <summary>
        /// Per-socket metadata extracted from a <c>cast_start</c> event.
        /// </summary>
        public sealed class SocketMetadataEntry
        {
            public string? SocketName { get; init; }
            public string? Type { get; init; }
            public string? MateriaName { get; init; }
            public bool MultiTurn { get; init; }
        }
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
            Message = "[pi stdout] ({RunId}) unrecognized type='{Type}' (castId={CastId}, materia={MateriaName}): {RawLine}"
        )]
        public static partial void PiStdoutUnknownType(
            ILogger logger,
            string runId,
            string type,
            string castId,
            string materiaName,
            string rawLine
        );

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "[pi stdout] ({RunId}) intermediate type='{Type}'"
        )]
        public static partial void PiStdoutIntermediate(
            ILogger logger,
            string runId,
            string type
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

        // ── RPC logging surface ─────────────────────────────────
        // These logs make the RPC calls to the autonomous Elene runtime
        // visible from agent-router. Request/response at debug, dispatch
        // failures at error.

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "[rpc] Prompt sent to pi — runId={RunId}, promptId={PromptId}, message='{Message}'.")]
        public static partial void RpcPromptSent(
            ILogger logger, string runId, string promptId, string message);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "[rpc] Response from pi — runId={RunId}, command={Command}, success={Success}.")]
        public static partial void RpcResponseReceived(
            ILogger logger, string runId, string command, bool success);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "[pi stdout] agent_end received for run '{RunId}'; agent single-turn cast is complete.")]
        public static partial void PiAgentEndReceived(ILogger logger, string runId);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "agent_end received for run '{RunId}'; initiating graceful shutdown of pi process.")]
        public static partial void AgentEndInitiatingShutdown(ILogger logger, string runId);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Contract drift detected on process exit for run '{RunId}': unrecognized type '{UnrecognizedType}'. Failing the run.")]
        public static partial void ContractDriftOnExit(
            ILogger logger, string runId, string unrecognizedType);

        [LoggerMessage(
            Level = LogLevel.Error,
            Message = "multiTurn agent socket(s) detected in cast_start for run '{RunId}': {MultiTurnSocketNames}. " +
                      "The agent-controller eventing preset is active and cannot drive multiTurn agent sockets.")]
        public static partial void MultiTurnAgentSocketsDetected(
            ILogger logger, string runId, string multiTurnSocketNames);
    }
}

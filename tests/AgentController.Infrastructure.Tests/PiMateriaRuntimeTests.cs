using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentController.Application;
using AgentController.Application.Services;
using AgentController.Domain;
using AgentController.Infrastructure;
using AgentController.Infrastructure.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit.Abstractions;

namespace AgentController.Infrastructure.Tests;

/// <summary>
/// Integration tests for <see cref="PiMateriaRuntime"/> using a fake <c>pi</c>
/// process and a real HTTP listener standing in for the controller's
/// <c>POST /runs/{runId}/events</c> endpoint.
///
/// These validate the full round trip that the production runtime depends on:
/// <list type="bullet">
///   <item>The runtime spawns the configured executable in RPC mode.</item>
///   <item>It sends the cast as an RPC prompt and waits for acceptance.</item>
///   <item>The fake pi POSTs <c>runtime.*</c> events over HTTP
///       (<c>CONTROLLER_EVENT_URL</c>), which the listener ingests through the
///       real <see cref="IRunLifecycleService"/>.</item>
///   <item>The runtime detects the run going terminal via the webhook and shuts
///       the process down.</item>
///   <item>If the process dies without a final event, a terminal event is
///       synthesized from the exit code.</item>
///   <item><see cref="IAgentRuntime.CancelAsync"/> tears the process down.</item>
/// </list>
///
/// No LLM is involved — the fake pi script posts canned events. This makes the
/// test deterministic and CI-friendly while exercising the real process,
/// environment, and HTTP plumbing.
/// </summary>
public class PiMateriaRuntimeTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private ServiceProvider _provider = null!;
    private IServiceScopeFactory _scopeFactory = null!;
    private InMemoryAgentRunStore _runStore = null!;
    private InMemoryLifecycleEventStore _eventStore = null!;
    private string _tempRoot = null!;
    private string _fakePiPath = null!;

    public PiMateriaRuntimeTests(ITestOutputHelper output) => _output = output;

    public Task InitializeAsync()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"pimateria-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);

        _fakePiPath = Path.Combine(_tempRoot, "fake-pi.py");
        // The default happy-path fake pi. Per-test variants overwrite the script
        // at the same path before starting the runtime.
        WriteFakePiScript(_fakePiPath, FakePiVariant.HappyPr);

        var services = new ServiceCollection();
        services.AddSingleton<InMemoryAgentRunStore>();
        services.AddSingleton<InMemoryLifecycleEventStore>();
        services.AddSingleton<InMemoryWorkItemStore>();
        services.AddSingleton<IAgentRunStore>(sp => sp.GetRequiredService<InMemoryAgentRunStore>());
        services.AddSingleton<ILifecycleEventStore>(sp =>
            sp.GetRequiredService<InMemoryLifecycleEventStore>()
        );
        services.AddSingleton<IWorkItemStore>(sp => sp.GetRequiredService<InMemoryWorkItemStore>());
        services.AddSingleton<IWorkSource, StubWorkSource>();
        services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning));
        services.AddScoped<IRunLifecycleService, RunLifecycleService>();
        services.AddSingleton<IAgentRuntime, PiMateriaRuntime>();

        _provider = services.BuildServiceProvider();
        _scopeFactory = _provider.GetRequiredService<IServiceScopeFactory>();
        _runStore = _provider.GetRequiredService<InMemoryAgentRunStore>();
        _eventStore = _provider.GetRequiredService<InMemoryLifecycleEventStore>();

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        // Ensure any runtime-created processes are torn down.
        if (_provider.GetService<IAgentRuntime>() is IDisposable disposable)
        {
            disposable.Dispose();
        }

        await _provider.DisposeAsync();

        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
            // Best-effort.
        }
    }

    // ── Happy path: fake pi webhooks drive the run to PrOpened ───────

    [Fact]
    public async Task StartAsync_FakePiPostsEvents_RunReachesPrOpened()
    {
        await using var harness = await NewHarnessAsync(_fakePiPath);

        var spec = await BuildSpecAsync(harness, "Implement demo feature");

        var runtime = harness.Runtime!;
        var handle = await runtime.StartAsync(spec, CancellationToken.None);

        Assert.Equal(spec.RunId, handle.RunId);
        Assert.StartsWith("pi-", handle.RuntimeRunId, StringComparison.Ordinal);

        // The fake pi posts accepted → status → completed(pull_request_opened)
        // and stays alive until the controller shuts it down.
        await WaitForStateAsync(
            spec.RunId,
            s => s == RunLifecycleState.PrOpened,
            TimeSpan.FromSeconds(15)
        );

        var run = await _runStore.GetByIdAsync(spec.RunId, CancellationToken.None);
        Assert.NotNull(run);
        Assert.Equal(RunLifecycleState.PrOpened, run!.Status);
        Assert.NotNull(run.PullRequestUrl);

        var events = await _eventStore.ListByRunIdAsync(spec.RunId, CancellationToken.None);
        Assert.Contains(events, e => e.EventType == RuntimeEventTypes.Accepted);
        Assert.Contains(events, e => e.EventType == RuntimeEventTypes.Completed);
    }

    // ── Crash path: pi exits non-zero without a final event ──────────

    [Fact]
    public async Task StartAsync_FakePiCrashesWithoutFinalEvent_SynthesizesFailure()
    {
        // Overwrite the fake pi with one that accepts nothing and exits 1.
        WriteFakePiScript(_fakePiPath, FakePiVariant.Crash);

        await using var harness = await NewHarnessAsync(_fakePiPath);
        var spec = await BuildSpecAsync(harness, "Crash test");

        var runtime = harness.Runtime!;
        _ = await runtime.StartAsync(spec, CancellationToken.None);

        await WaitForStateAsync(
            spec.RunId,
            s => s == RunLifecycleState.Failed,
            TimeSpan.FromSeconds(15)
        );

        var run = await _runStore.GetByIdAsync(spec.RunId, CancellationToken.None);
        Assert.NotNull(run);
        Assert.Equal(RunLifecycleState.Failed, run!.Status);

        var events = await _eventStore.ListByRunIdAsync(spec.RunId, CancellationToken.None);
        Assert.Contains(
            events,
            e =>
                e.EventType == RuntimeEventTypes.Failed
                && e.Payload is { } p
                && p.TryGetValue("synthesized", out var synth)
                && synth is true
        );
    }

    // ── Cancellation tears the process down ──────────────────────────

    [Fact]
    public async Task CancelAsync_StopsAndKillsFakePi()
    {
        WriteFakePiScript(_fakePiPath, FakePiVariant.Idle);

        await using var harness = await NewHarnessAsync(_fakePiPath);
        var spec = await BuildSpecAsync(harness, "Cancel test");

        var runtime = harness.Runtime!;
        var handle = await runtime.StartAsync(spec, CancellationToken.None);

        // Give the process a moment to spawn and the runtime to start polling.
        await Task.Delay(TimeSpan.FromMilliseconds(500));

        var before = await _runStore.GetByIdAsync(spec.RunId, CancellationToken.None);
        Assert.NotNull(before);
        Assert.True(
            before!.Status == RunLifecycleState.AgentRunning
                || before.Status == RunLifecycleState.AwaitingResult,
            $"Expected non-terminal state before cancel, got {before.Status}."
        );

        await runtime.CancelAsync(handle, CancellationToken.None);

        // The runtime ingests runtime.cancelled after killing the process.
        await WaitForStateAsync(
            spec.RunId,
            s => s == RunLifecycleState.Cancelled,
            TimeSpan.FromSeconds(15)
        );

        var run = await _runStore.GetByIdAsync(spec.RunId, CancellationToken.None);
        Assert.NotNull(run);
        Assert.Equal(RunLifecycleState.Cancelled, run!.Status);
    }

    // ── Missing ControllerBaseUrl synthesizes a failure ──────────────

    [Fact]
    public async Task StartAsync_MissingControllerBaseUrl_SynthesizesFailure()
    {
        var runtime = new PiMateriaRuntime(
            _scopeFactory,
            new StaticOptionsMonitor<RuntimeOptions>(
                new RuntimeOptions
                {
                    Provider = "PiMateria",
                    PiExecutablePath = _fakePiPath,
                    // ControllerBaseUrl intentionally unset
                    PromptAcceptanceTimeoutSeconds = 2,
                    CancelGracePeriodSeconds = 1,
                }
            ),
            _provider.GetRequiredService<ILogger<PiMateriaRuntime>>()
        );

        var runId = await SeedRunAsync();
        var spec = new AgentRunSpec
        {
            RunId = runId,
            WorkRef = new ExternalWorkRef { Source = "LocalFile", ExternalId = "x" },
            EnvironmentHandle = new EnvironmentHandle { RootPath = _tempRoot },
            RepoCheckout = new RepositoryCheckout { LocalPath = _tempRoot },
        };

        var handle = await runtime.StartAsync(spec, CancellationToken.None);
        Assert.NotNull(handle.RuntimeRunId);

        await WaitForStateAsync(runId, s => s == RunLifecycleState.Failed, TimeSpan.FromSeconds(5));

        runtime.Dispose();
    }

    // ── DI registration smoke test ───────────────────────────────────

    [Fact]
    public void AddAgentControllerPiMateriaRuntime_RegistersSingleton()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IOptionsMonitor<RuntimeOptions>>(
            _ => new StaticOptionsMonitor<RuntimeOptions>(
                new RuntimeOptions { Provider = "PiMateria" }
            )
        );
        services.AddLogging();

        services.AddAgentControllerPiMateriaRuntime();
        var provider = services.BuildServiceProvider();

        var runtime1 = provider.GetRequiredService<IAgentRuntime>();
        var runtime2 = provider.GetRequiredService<IAgentRuntime>();

        Assert.Same(runtime1, runtime2);
        Assert.IsType<PiMateriaRuntime>(runtime1);
    }

    // ── Regression E2E: agent_end per-socket non-terminal handling ──
    //
    // This test exercises the multi-socket cast path that stalled in the original
    // E2E run. agent_end is per-socket and non-terminal — the runtime must NOT
    // shut down on agent_end. The fake pi exits cleanly, and the process exit
    // handler synthesizes a terminal event from exit code 0.
    //
    // The original stall occurred because the first agent_end (from one socket)
    // was misread as overall completion, orphaning downstream sockets in an
    // AwaitingResult state.

    [Fact]
    public async Task StartAsync_AgentEndNonTerminal_RunCompletesWithoutStall()
    {
        // Overwrite the fake pi with one that emits the full cast lifecycle
        // (cast_start → materia_start → materia_end → agent_end) under the
        // agent-controller preset, then exits cleanly.
        WriteFakePiScript(_fakePiPath, FakePiVariant.AgentEndTerminalRegression);

        await using var harness = await NewHarnessAsync(_fakePiPath);
        var spec = await BuildSpecAsync(harness, "Agent end terminal regression");

        var runtime = harness.Runtime!;
        _ = await runtime.StartAsync(spec, CancellationToken.None);

        // agent_end is per-socket and non-terminal — the runtime stays alive.
        // The fake pi exits cleanly, and the process exit handler synthesizes
        // a terminal event from exit code 0 (→ Completed).
        await WaitForStateAsync(
            spec.RunId,
            s => s == RunLifecycleState.Completed || s == RunLifecycleState.Failed,
            TimeSpan.FromSeconds(15)
        );

        var run = await _runStore.GetByIdAsync(spec.RunId, CancellationToken.None);
        Assert.NotNull(run);
        // The run should reach a terminal state (Completed from exit 0 synthesis).
        Assert.True(
            run!.Status == RunLifecycleState.Completed || run.Status == RunLifecycleState.Failed,
            $"Expected terminal state, got {run.Status}. " +
            "The runtime should recognize agent_end as non-terminal and not stall."
        );

        // Verify no unrecognized-type warnings — agent_end must be recognized.
        var events = await _eventStore.ListByRunIdAsync(spec.RunId, CancellationToken.None);
        Assert.DoesNotContain(
            events,
            e => e.Message != null && e.Message.Contains("unrecognized", StringComparison.OrdinalIgnoreCase)
        );

        // Verify the cast_start artifact was ingested with socket metadata.
        var castStartEvents = events.Where(e =>
            e.EventType == RuntimeEventTypes.Status &&
            e.Payload is { } p &&
            p.ContainsKey("sockets")
        ).ToList();
        Assert.NotEmpty(castStartEvents);

        // Verify the cast_start payload has the agent-controller preset and single-turn sockets.
        var castStartPayload = castStartEvents.First().Payload!;
        Assert.True(
            castStartPayload.TryGetValue("hasMultiTurnAgentSockets", out var hasMultiTurn) &&
            hasMultiTurn is false,
            "Expected cast_start artifact to flag hasMultiTurnAgentSockets=false"
        );

        // Verify the run reached a terminal state without stalling.
        // Key regression assertion: the original E2E aborted mid-flight because
        // the first agent_end (from one socket) was misread as overall completion,
        // orphaning downstream sockets. Post-fix, agent_end is per-socket and
        // non-terminal — the runtime stays alive for subsequent sockets.
        Assert.True(
            run!.Status == RunLifecycleState.Completed || run.Status == RunLifecycleState.Failed,
            $"Run stalled! Final state: {run.Status}. " +
            "This is the exact regression from the original E2E — agent_end was treated as terminal."
        );
    }

    // ── agent_end: recognized per-socket non-terminal event ──

    [Fact]
    public async Task StartAsync_FakePiEmitsAgentEnd_RunCompletesWithoutStall()
    {
        // Overwrite the fake pi with one that emits agent_end on stdout
        // and exits cleanly (no webhook terminal event).
        WriteFakePiScript(_fakePiPath, FakePiVariant.AgentEnd);

        await using var harness = await NewHarnessAsync(_fakePiPath);
        var spec = await BuildSpecAsync(harness, "Agent end test");

        var runtime = harness.Runtime!;
        _ = await runtime.StartAsync(spec, CancellationToken.None);

        // agent_end is per-socket and non-terminal — the runtime stays alive.
        // The fake pi exits cleanly, and the process exit handler synthesizes
        // a terminal event from exit code 0 (→ Completed).
        await WaitForStateAsync(
            spec.RunId,
            s => s == RunLifecycleState.Completed || s == RunLifecycleState.Failed,
            TimeSpan.FromSeconds(15)
        );

        var run = await _runStore.GetByIdAsync(spec.RunId, CancellationToken.None);
        Assert.NotNull(run);
        // The run should reach a terminal state (Completed from exit 0 synthesis).
        Assert.True(
            run!.Status == RunLifecycleState.Completed || run.Status == RunLifecycleState.Failed,
            $"Expected terminal state, got {run.Status}." +
            $" The runtime should recognize agent_end as non-terminal and not stall."
        );

        // Verify no unrecognized-type warnings in the events.
        var events = await _eventStore.ListByRunIdAsync(spec.RunId, CancellationToken.None);
        Assert.DoesNotContain(
            events,
            e => e.Message != null && e.Message.Contains("unrecognized", StringComparison.OrdinalIgnoreCase)
        );
    }

    // ── Multi-socket agent_end: status events and socket-local tracking ──

    [Fact]
    public async Task StartAsync_MultiSocketAgentEnd_EmitsSocketCompletionStatusEvents()
    {
        // Overwrite the fake pi with one that emits agent_end for 3 sockets,
        // each with a socketName field.
        WriteFakePiScript(_fakePiPath, FakePiVariant.MultiSocketAgentEnd);

        await using var harness = await NewHarnessAsync(_fakePiPath);
        var spec = await BuildSpecAsync(harness, "Multi-socket agent end test");

        var runtime = harness.Runtime!;
        _ = await runtime.StartAsync(spec, CancellationToken.None);

        // Wait for the run to reach a terminal state (synthesized from exit 0).
        await WaitForStateAsync(
            spec.RunId,
            s => s == RunLifecycleState.Completed || s == RunLifecycleState.Failed,
            TimeSpan.FromSeconds(15)
        );

        var run = await _runStore.GetByIdAsync(spec.RunId, CancellationToken.None);
        Assert.NotNull(run);
        Assert.True(
            run!.Status == RunLifecycleState.Completed || run.Status == RunLifecycleState.Failed,
            $"Expected terminal state, got {run.Status}."
        );

        // Verify socket completion status events were emitted.
        var events = await _eventStore.ListByRunIdAsync(spec.RunId, CancellationToken.None);

        // Find all runtime.status events with "Socket ... completed" messages.
        var socketCompletionEvents = events.Where(e =>
            e.EventType == RuntimeEventTypes.Status &&
            e.Message != null &&
            e.Message.Contains("Socket", StringComparison.Ordinal) &&
            e.Message.Contains("completed", StringComparison.OrdinalIgnoreCase)
        ).ToList();

        // Should have 3 socket completion events (one per agent_end).
        Assert.Equal(3, socketCompletionEvents.Count);

        // Verify each socket name appears in a completion event.
        var socketCompletionMessages = socketCompletionEvents.Select(e => e.Message!).ToList();
        Assert.Contains(socketCompletionMessages, m => m.Contains("Socket-3", StringComparison.Ordinal));
        Assert.Contains(socketCompletionMessages, m => m.Contains("Socket-4", StringComparison.Ordinal));
        Assert.Contains(socketCompletionMessages, m => m.Contains("Socket-5", StringComparison.Ordinal));

        // Verify the payload includes socketName and completedSockets.
        foreach (var evt in socketCompletionEvents)
        {
            Assert.NotNull(evt.Payload);
            Assert.True(evt.Payload!.ContainsKey("socketName"));
            Assert.True(evt.Payload!.ContainsKey("completedSockets"));
        }
    }

    // ── Config-driven autonomous-mode: derive from controller-owned config ──
    //
    // The runtime derives autonomous-mode from the controller-owned
    // materia-controller.json config at startup, not from the cast_start
    // stdout event. This removes the startup-ordering race where the runtime
    // aborted because cast_start (the signal it was keying off of) was not
    // yet present.

    [Fact]
    public async Task StartAsync_ControllerWrittenConfig_HasAutonomousModeTrue()
    {
        // The controller writes materia-controller.json with autonomousMode: true.
        // Verify the written config file has the expected field.
        WriteFakePiScript(_fakePiPath, FakePiVariant.Idle);

        await using var harness = await NewHarnessAsync(_fakePiPath);
        var spec = await BuildSpecAsync(harness, "Config autonomous mode test");

        var runtime = harness.Runtime!;
        var handle = await runtime.StartAsync(spec, CancellationToken.None);

        // Give the runtime a moment to write the config.
        await Task.Delay(TimeSpan.FromMilliseconds(500));

        // The controller writes the config to context/materia-controller.json.
        // EnvironmentHandle.RootPath is envDir (which includes runId).
        var contextDir = Path.Combine(spec.EnvironmentHandle.RootPath, "context");
        var configPath = Path.Combine(contextDir, "materia-controller.json");

        Assert.True(File.Exists(configPath), $"Config file should exist at {configPath}");

        var configJson = File.ReadAllText(configPath);
        using var doc = JsonDocument.Parse(configJson);

        Assert.True(
            doc.RootElement.TryGetProperty("autonomousMode", out var amEl),
            "Config should have autonomousMode field"
        );
        Assert.True(amEl.GetBoolean(), "autonomousMode should be true in controller-written config");

        await runtime.CancelAsync(handle, CancellationToken.None);
        runtime.Dispose();
    }

    [Fact]
    public async Task StartAsync_CustomConfigWithAutonomousModeFalse_AllowsMultiTurnSockets()
    {
        // When the operator provides a custom config with autonomousMode: false,
        // the runtime should treat the run as interactive (non-autonomous).
        // Under non-autonomous mode, multiTurn agent sockets are allowed
        // (the fail-fast guard only applies to autonomous runs).
        var customConfigPath = Path.Combine(_tempRoot, "custom-config.json");
        File.WriteAllText(customConfigPath, @"{
            ""activeLoadout"": ""Wedge"",
            ""autonomousMode"": false,
            ""eventing"": {""enabled"": true, ""presets"": [""interactive""], ""heartbeatIntervalMs"": 30000}
        }");

        WriteFakePiScript(_fakePiPath, FakePiVariant.MultiTurnAgentSocket);

        await using var harness = await NewHarnessAsync(_fakePiPath);

        var runId = await SeedRunAsync();
        var envDir = Path.Combine(_tempRoot, runId);
        var contextDir = Path.Combine(envDir, "context");
        var repoDir = Path.Combine(envDir, "repo");
        Directory.CreateDirectory(contextDir);
        Directory.CreateDirectory(repoDir);
        await File.WriteAllTextAsync(
            Path.Combine(contextDir, "work-item.md"),
            "# Custom config test\n\nSome acceptance criteria.\n"
        );

        var runtime = new PiMateriaRuntime(
            _scopeFactory,
            new StaticOptionsMonitor<RuntimeOptions>(
                new RuntimeOptions
                {
                    Provider = "PiMateria",
                    PiExecutablePath = harness.FakePiPath,
                    ControllerBaseUrl = harness.BaseUrl,
                    MateriaConfigPath = customConfigPath,
                    HeartbeatIntervalSeconds = 2,
                    PromptAcceptanceTimeoutSeconds = 4,
                    CancelGracePeriodSeconds = 3,
                }
            ),
            _provider.GetRequiredService<ILogger<PiMateriaRuntime>>()
        );

        var spec = new AgentRunSpec
        {
            RunId = runId,
            WorkRef = new ExternalWorkRef { Source = "LocalFile", ExternalId = "test-custom-config" },
            EnvironmentHandle = new EnvironmentHandle { RootPath = envDir },
            RepoCheckout = new RepositoryCheckout { LocalPath = repoDir, RepoKey = "widget" },
        };

        _ = await runtime.StartAsync(spec, CancellationToken.None);

        // Under non-autonomous mode (autonomousMode: false), multiTurn sockets
        // are allowed. The fake pi exits cleanly, and the process exit handler
        // synthesizes a terminal event from exit code 0.
        await WaitForStateAsync(
            runId,
            s => s == RunLifecycleState.Completed || s == RunLifecycleState.Failed,
            TimeSpan.FromSeconds(15)
        );

        var run = await _runStore.GetByIdAsync(runId, CancellationToken.None);
        Assert.NotNull(run);
        // The run should reach Completed (from exit 0 synthesis), not Failed
        // from multiTurn socket rejection.
        Assert.Equal(RunLifecycleState.Completed, run!.Status);

        runtime.Dispose();
    }

    [Fact]
    public async Task StartAsync_MissingConfigFile_DefaultsToNonAutonomous()
    {
        // When MateriaConfigPath points to a non-existent file, the runtime
        // should default to non-autonomous mode (no fail-fast on multiTurn).
        var nonexistentConfigPath = Path.Combine(_tempRoot, "nonexistent-config.json");
        Assert.False(File.Exists(nonexistentConfigPath));

        WriteFakePiScript(_fakePiPath, FakePiVariant.MultiTurnAgentSocket);

        await using var harness = await NewHarnessAsync(_fakePiPath);

        var runId = await SeedRunAsync();
        var envDir = Path.Combine(_tempRoot, runId);
        var contextDir = Path.Combine(envDir, "context");
        var repoDir = Path.Combine(envDir, "repo");
        Directory.CreateDirectory(contextDir);
        Directory.CreateDirectory(repoDir);
        await File.WriteAllTextAsync(
            Path.Combine(contextDir, "work-item.md"),
            "# Missing config test\n\nSome acceptance criteria.\n"
        );

        var runtime = new PiMateriaRuntime(
            _scopeFactory,
            new StaticOptionsMonitor<RuntimeOptions>(
                new RuntimeOptions
                {
                    Provider = "PiMateria",
                    PiExecutablePath = harness.FakePiPath,
                    ControllerBaseUrl = harness.BaseUrl,
                    MateriaConfigPath = nonexistentConfigPath,
                    HeartbeatIntervalSeconds = 2,
                    PromptAcceptanceTimeoutSeconds = 4,
                    CancelGracePeriodSeconds = 3,
                }
            ),
            _provider.GetRequiredService<ILogger<PiMateriaRuntime>>()
        );

        var spec = new AgentRunSpec
        {
            RunId = runId,
            WorkRef = new ExternalWorkRef { Source = "LocalFile", ExternalId = "test-missing-config" },
            EnvironmentHandle = new EnvironmentHandle { RootPath = envDir },
            RepoCheckout = new RepositoryCheckout { LocalPath = repoDir, RepoKey = "widget" },
        };

        _ = await runtime.StartAsync(spec, CancellationToken.None);

        // Under non-autonomous mode (default when config is missing), multiTurn
        // sockets are allowed. The fake pi exits cleanly, and the process exit
        // handler synthesizes a terminal event from exit code 0.
        await WaitForStateAsync(
            runId,
            s => s == RunLifecycleState.Completed || s == RunLifecycleState.Failed,
            TimeSpan.FromSeconds(15)
        );

        var run = await _runStore.GetByIdAsync(runId, CancellationToken.None);
        Assert.NotNull(run);
        Assert.Equal(RunLifecycleState.Completed, run!.Status);

        runtime.Dispose();
    }

    // ── Unrecognized stdout event type: fail-closed under agent-controller ──

    [Fact]
    public async Task StartAsync_FakePiEmitsUnrecognizedType_RunFailsWithContractDriftError()
    {
        // Overwrite the fake pi with one that emits an unrecognized event type.
        WriteFakePiScript(_fakePiPath, FakePiVariant.UnrecognizedType);

        await using var harness = await NewHarnessAsync(_fakePiPath);
        var spec = await BuildSpecAsync(harness, "Unrecognized type test");

        var runtime = harness.Runtime!;
        _ = await runtime.StartAsync(spec, CancellationToken.None);

        // The runtime should detect the unrecognized type and fail the run
        // with a contract-drift error instead of stalling.
        await WaitForStateAsync(
            spec.RunId,
            s => s == RunLifecycleState.Failed,
            TimeSpan.FromSeconds(15)
        );

        var run = await _runStore.GetByIdAsync(spec.RunId, CancellationToken.None);
        Assert.NotNull(run);
        Assert.Equal(RunLifecycleState.Failed, run!.Status);

        // Verify the failure mentions the unrecognized type and includes diagnostics.
        var events = await _eventStore.ListByRunIdAsync(spec.RunId, CancellationToken.None);
        var failureEvents = events.Where(e => e.EventType == RuntimeEventTypes.Failed).ToList();
        Assert.NotEmpty(failureEvents);
        var failureEvent = failureEvents.First();

        // The message should mention the unrecognized type.
        Assert.True(
            failureEvent.Message != null && failureEvent.Message.Contains("unrecognized", StringComparison.OrdinalIgnoreCase),
            $"Expected failure message to mention 'unrecognized' type. Got: {failureEvent.Message}"
        );

        // The message should include the cast id and materia name for diagnostics.
        Assert.True(
            failureEvent.Message != null && failureEvent.Message.Contains("castId="),
            $"Expected failure message to include castId. Got: {failureEvent.Message}"
        );
        Assert.True(
            failureEvent.Message != null && failureEvent.Message.Contains("materia="),
            $"Expected failure message to include materia name. Got: {failureEvent.Message}"
        );

        // The payload should contain structured diagnostic fields.
        Assert.NotNull(failureEvent.Payload);
        Assert.True(
            failureEvent.Payload!.ContainsKey("unrecognizedType"),
            $"Expected payload to contain 'unrecognizedType'. Keys: {string.Join(", ", failureEvent.Payload!.Keys)}"
        );
        Assert.True(
            failureEvent.Payload!.ContainsKey("castId"),
            $"Expected payload to contain 'castId'. Keys: {string.Join(", ", failureEvent.Payload!.Keys)}"
        );
        Assert.True(
            failureEvent.Payload!.ContainsKey("materiaName"),
            $"Expected payload to contain 'materiaName'. Keys: {string.Join(", ", failureEvent.Payload!.Keys)}"
        );
    }

    // ── (interactive) Unrecognized type under interactive eventing: warn-and-continue ──

    [Fact]
    public async Task StartAsync_UnrecognizedTypeUnderInteractiveEventing_RunCompletes()
    {
        // Overwrite the fake pi with one that emits an unrecognized event type
        // under the "interactive" eventing preset. The runtime should warn
        // but NOT fail the run — a human is present to handle the situation.
        WriteFakePiScript(_fakePiPath, FakePiVariant.UnrecognizedTypeInteractive);

        await using var harness = await NewHarnessAsync(_fakePiPath);
        var spec = await BuildSpecAsync(harness, "Unrecognized type interactive test");

        var runtime = harness.Runtime!;
        _ = await runtime.StartAsync(spec, CancellationToken.None);

        // The runtime should allow the run to complete (warn-and-continue)
        // because the eventing preset is "interactive" not "agent-controller".
        await WaitForStateAsync(
            spec.RunId,
            s => s == RunLifecycleState.Completed || s == RunLifecycleState.Failed,
            TimeSpan.FromSeconds(15)
        );

        var run = await _runStore.GetByIdAsync(spec.RunId, CancellationToken.None);
        Assert.NotNull(run);
        // The run should reach Completed (from the webhook), NOT Failed.
        Assert.Equal(RunLifecycleState.Completed, run!.Status);

        // Verify no contract-drift failure events.
        var events = await _eventStore.ListByRunIdAsync(spec.RunId, CancellationToken.None);
        Assert.DoesNotContain(
            events,
            e => e.EventType == RuntimeEventTypes.Failed &&
                 e.Message != null &&
                 e.Message.Contains("unrecognized", StringComparison.OrdinalIgnoreCase)
        );
    }

    // ── MultiTurn agent socket: fail-fast under agent-controller ──────

    [Fact]
    public async Task StartAsync_FakePiEmitsCastStartWithMultiTurnSocket_RunFailsFast()
    {
        // Overwrite the fake pi with one that emits a cast_start event
        // containing a multiTurn agent socket.
        WriteFakePiScript(_fakePiPath, FakePiVariant.MultiTurnAgentSocket);

        await using var harness = await NewHarnessAsync(_fakePiPath);
        var spec = await BuildSpecAsync(harness, "MultiTurn socket test");

        var runtime = harness.Runtime!;
        _ = await runtime.StartAsync(spec, CancellationToken.None);

        // The runtime should detect the multiTurn agent socket in the
        // cast_start event and fail the run immediately with a clear error.
        await WaitForStateAsync(
            spec.RunId,
            s => s == RunLifecycleState.Failed,
            TimeSpan.FromSeconds(15)
        );

        var run = await _runStore.GetByIdAsync(spec.RunId, CancellationToken.None);
        Assert.NotNull(run);
        Assert.Equal(RunLifecycleState.Failed, run!.Status);

        // Verify the failure mentions multiTurn and the offending socket name.
        var events = await _eventStore.ListByRunIdAsync(spec.RunId, CancellationToken.None);
        var failureEvents = events.Where(e => e.EventType == RuntimeEventTypes.Failed).ToList();
        Assert.NotEmpty(failureEvents);
        var failureEvent = failureEvents.First();
        Assert.True(
            failureEvent.Message != null && failureEvent.Message.Contains("multiTurn", StringComparison.OrdinalIgnoreCase),
            $"Expected failure message to mention 'multiTurn'. Got: {failureEvent.Message}"
        );
        Assert.True(
            failureEvent.Message != null && failureEvent.Message.Contains("Socket-3", StringComparison.Ordinal),
            $"Expected failure message to name the offending socket 'Socket-3'. Got: {failureEvent.Message}"
        );

        // Verify the cast_start artifact was ingested with socket metadata.
        // The cast_start is persisted as a runtime.status event (informational no-op)
        // with enriched socket metadata in the payload.
        var allEvents = await _eventStore.ListByRunIdAsync(spec.RunId, CancellationToken.None);
        var castStartEvents = allEvents.Where(e =>
            e.EventType == RuntimeEventTypes.Status &&
            e.Payload is { } p &&
            p.ContainsKey("sockets")
        ).ToList();
        Assert.NotEmpty(castStartEvents);
        var castStartEvent = castStartEvents.First();
        Assert.NotNull(castStartEvent.Payload);
        Assert.True(
            castStartEvent.Payload!.ContainsKey("sockets"),
            $"Expected cast_start artifact to contain 'sockets' payload. Payload keys: {string.Join(", ", castStartEvent.Payload!.Keys)}"
        );
        var socketsPayload = castStartEvent.Payload!["sockets"];
        Assert.NotNull(socketsPayload);
        Assert.True(
            castStartEvent.Payload!.TryGetValue("hasMultiTurnAgentSockets", out var hasMultiTurn) &&
            hasMultiTurn is true,
            $"Expected cast_start artifact to flag hasMultiTurnAgentSockets=true"
        );
    }

    // ── (a) Single-turn agent socket under agent-controller: allowed through ──

    [Fact]
    public async Task StartAsync_SingleTurnAgentSocketUnderAgentController_RunCompletes()
    {
        // Overwrite the fake pi with one that emits a cast_start with only
        // single-turn agent sockets under the agent-controller preset.
        WriteFakePiScript(_fakePiPath, FakePiVariant.SingleTurnAgentSocket);

        await using var harness = await NewHarnessAsync(_fakePiPath);
        var spec = await BuildSpecAsync(harness, "Single-turn socket test");

        var runtime = harness.Runtime!;
        _ = await runtime.StartAsync(spec, CancellationToken.None);

        // The runtime should allow single-turn agent sockets under agent-controller
        // and the run should reach a terminal state via the webhook.
        await WaitForStateAsync(
            spec.RunId,
            s => s == RunLifecycleState.Completed || s == RunLifecycleState.Failed,
            TimeSpan.FromSeconds(15)
        );

        var run = await _runStore.GetByIdAsync(spec.RunId, CancellationToken.None);
        Assert.NotNull(run);
        // The run should reach Completed (from the webhook), not Failed.
        Assert.Equal(RunLifecycleState.Completed, run!.Status);

        // Verify no multiTurn-related failure.
        var events = await _eventStore.ListByRunIdAsync(spec.RunId, CancellationToken.None);
        Assert.DoesNotContain(
            events,
            e => e.EventType == RuntimeEventTypes.Failed &&
                 e.Message != null &&
                 e.Message.Contains("multiTurn", StringComparison.OrdinalIgnoreCase)
        );
    }

    // ── (c) MultiTurn socket under non-agent-controller: allowed through ──

    [Fact]
    public async Task StartAsync_MultiTurnSocketUnderInteractiveEventing_RunCompletes()
    {
        // Overwrite the fake pi with one that emits a cast_start with a multiTurn
        // agent socket under the "interactive" preset (not agent-controller).
        WriteFakePiScript(_fakePiPath, FakePiVariant.MultiTurnInteractive);

        await using var harness = await NewHarnessAsync(_fakePiPath);
        var spec = await BuildSpecAsync(harness, "MultiTurn interactive test");

        var runtime = harness.Runtime!;
        _ = await runtime.StartAsync(spec, CancellationToken.None);

        // The runtime should allow multiTurn sockets under non-agent-controller
        // eventing (the fail-fast guard only applies to agent-controller preset).
        await WaitForStateAsync(
            spec.RunId,
            s => s == RunLifecycleState.Completed || s == RunLifecycleState.Failed,
            TimeSpan.FromSeconds(15)
        );

        var run = await _runStore.GetByIdAsync(spec.RunId, CancellationToken.None);
        Assert.NotNull(run);
        // The run should reach Completed (from the webhook), not Failed.
        Assert.Equal(RunLifecycleState.Completed, run!.Status);

        // Verify no multiTurn-related failure.
        var events = await _eventStore.ListByRunIdAsync(spec.RunId, CancellationToken.None);
        Assert.DoesNotContain(
            events,
            e => e.EventType == RuntimeEventTypes.Failed &&
                 e.Message != null &&
                 e.Message.Contains("multiTurn", StringComparison.OrdinalIgnoreCase)
        );
    }

    // ── Regression: Biggs loadout Socket-3 materia divergence ────────
    //
    // The "Biggs" loadout (user copy of default:full-auto) had Socket-3 bound
    // to Interactive-Plani (multiTurn=true) instead of Auto-Plan (multiTurn=false).
    // This caused agent-controller runs to stall because the controller never
    // sends /materia continue for multiTurn materias.
    //
    // See: docs/investigations/socket-3-materia-divergence.md

    [Fact]
    public async Task StartAsync_BiggsLoadoutSocket3Divergence_MultiTurnPlannerFailsFast()
    {
        // Simulate the exact cast_start from the Biggs loadout where Socket-3
        // is bound to Interactive-Plani (multiTurn=true) under agent-controller.
        WriteFakePiScript(_fakePiPath, FakePiVariant.BiggsLoadoutSocket3Divergence);

        await using var harness = await NewHarnessAsync(_fakePiPath);
        var spec = await BuildSpecAsync(harness, "Biggs loadout Socket-3 divergence regression");

        var runtime = harness.Runtime!;
        _ = await runtime.StartAsync(spec, CancellationToken.None);

        // The runtime must detect Socket-3's multiTurn agent socket and fail fast
        // under agent-controller eventing — preventing the silent stall that
        // occurred in the original incident.
        await WaitForStateAsync(
            spec.RunId,
            s => s == RunLifecycleState.Failed,
            TimeSpan.FromSeconds(15)
        );

        var run = await _runStore.GetByIdAsync(spec.RunId, CancellationToken.None);
        Assert.NotNull(run);
        Assert.Equal(RunLifecycleState.Failed, run!.Status);

        // Verify the failure names the offending socket and materia.
        var events = await _eventStore.ListByRunIdAsync(spec.RunId, CancellationToken.None);
        var failureEvents = events.Where(e => e.EventType == RuntimeEventTypes.Failed).ToList();
        Assert.NotEmpty(failureEvents);
        var failureEvent = failureEvents.First();

        // Must mention multiTurn (the root cause).
        Assert.True(
            failureEvent.Message != null && failureEvent.Message.Contains("multiTurn", StringComparison.OrdinalIgnoreCase),
            $"Expected failure to mention 'multiTurn'. Got: {failureEvent.Message}"
        );

        // Must name Socket-3 (the offending socket from the Biggs loadout).
        Assert.True(
            failureEvent.Message != null && failureEvent.Message.Contains("Socket-3", StringComparison.Ordinal),
            $"Expected failure to name 'Socket-3'. Got: {failureEvent.Message}"
        );

        // Verify the cast_start artifact was persisted with full socket metadata
        // so the divergence is diagnosable from the run artifact log.
        var castStartEvents = events.Where(e =>
            e.EventType == RuntimeEventTypes.Status &&
            e.Payload is { } p &&
            p.ContainsKey("sockets")
        ).ToList();
        Assert.NotEmpty(castStartEvents);
        var castStartPayload = castStartEvents.First().Payload!;

        // The persisted artifact should flag the multiTurn agent socket.
        Assert.True(
            castStartPayload.TryGetValue("hasMultiTurnAgentSockets", out var hasMultiTurn) &&
            hasMultiTurn is true,
            "Expected cast_start artifact to flag hasMultiTurnAgentSockets=true"
        );

        // The persisted artifact should contain socket metadata naming Interactive-Plani.
        Assert.True(
            castStartPayload.ContainsKey("sockets"),
            "Expected cast_start artifact to contain socket metadata for diagnosis"
        );
    }

    // ── Conformance: every contract event type is recognized ─────────

    [Fact]
    public async Task StartAsync_AllContractEventTypesRecognized_NoUnrecognizedWarnings()
    {
        // Conformance test: emit ALL recognized stdout event types from the
        // PiMateriaStdoutEventTypes contract. The runtime must handle every
        // one without triggering unrecognized-type warnings or failing the run.
        // This guards against contract drift between the emitter and parser.
        WriteFakePiScript(_fakePiPath, FakePiVariant.AllRecognizedTypes);

        await using var harness = await NewHarnessAsync(_fakePiPath);
        var spec = await BuildSpecAsync(harness, "Conformance test");

        var runtime = harness.Runtime!;
        _ = await runtime.StartAsync(spec, CancellationToken.None);

        // The runtime should recognize all event types and reach a terminal state.
        await WaitForStateAsync(
            spec.RunId,
            s => s == RunLifecycleState.Completed || s == RunLifecycleState.Failed,
            TimeSpan.FromSeconds(15)
        );

        var run = await _runStore.GetByIdAsync(spec.RunId, CancellationToken.None);
        Assert.NotNull(run);
        // The run should reach Completed (from the webhook), not Failed.
        Assert.Equal(RunLifecycleState.Completed, run!.Status);

        // Verify no unrecognized-type warnings in the events.
        // This is the conformance assertion: every type in the contract
        // (PiMateriaStdoutEventTypes) must be recognized by the parser.
        var events = await _eventStore.ListByRunIdAsync(spec.RunId, CancellationToken.None);
        Assert.DoesNotContain(
            events,
            e => e.Message != null && e.Message.Contains("unrecognized", StringComparison.OrdinalIgnoreCase)
        );

        // Verify no multiTurn-related failures (cast_start had only single-turn sockets).
        Assert.DoesNotContain(
            events,
            e => e.EventType == RuntimeEventTypes.Failed &&
                 e.Message != null &&
                 e.Message.Contains("multiTurn", StringComparison.OrdinalIgnoreCase)
        );
    }

    // ── Telemetry ignore list: telemetry types dropped, lifecycle types routed ──

    [Fact]
    public async Task StartAsync_TelemetryEventsIgnored_LifecycleEventsStillRoute()
    {
        // Emit all known telemetry-only pi-core event types plus real lifecycle
        // events. The telemetry types should be silently ignored (no unrecognized
        // warnings or run failures) and the lifecycle events should route correctly.
        WriteFakePiScript(_fakePiPath, FakePiVariant.TelemetryEventsIgnored);

        await using var harness = await NewHarnessAsync(_fakePiPath);
        var spec = await BuildSpecAsync(harness, "Telemetry ignore list test");

        var runtime = harness.Runtime!;
        _ = await runtime.StartAsync(spec, CancellationToken.None);

        // The runtime should recognize all lifecycle types and reach Completed.
        await WaitForStateAsync(
            spec.RunId,
            s => s == RunLifecycleState.Completed || s == RunLifecycleState.Failed,
            TimeSpan.FromSeconds(15)
        );

        var run = await _runStore.GetByIdAsync(spec.RunId, CancellationToken.None);
        Assert.NotNull(run);
        // The run should reach Completed (from the webhook), not Failed.
        // If telemetry types were treated as unrecognized, the run would fail.
        Assert.Equal(RunLifecycleState.Completed, run!.Status);

        // Verify no unrecognized-type warnings in the events.
        // Telemetry types must be silently ignored, not treated as contract drift.
        var events = await _eventStore.ListByRunIdAsync(spec.RunId, CancellationToken.None);
        Assert.DoesNotContain(
            events,
            e => e.Message != null && e.Message.Contains("unrecognized", StringComparison.OrdinalIgnoreCase)
        );

        // Verify the cast_start artifact was still ingested (lifecycle events route correctly).
        var castStartEvents = events.Where(e =>
            e.EventType == RuntimeEventTypes.Status &&
            e.Message != null &&
            e.Message.Contains("Cast started", StringComparison.OrdinalIgnoreCase)
        ).ToList();
        Assert.NotEmpty(castStartEvents);

        // Verify the agent_end socket-completion status event was emitted.
        var socketCompletedEvents = events.Where(e =>
            e.EventType == RuntimeEventTypes.Status &&
            e.Message != null &&
            e.Message.Contains("Socket", StringComparison.OrdinalIgnoreCase) &&
            e.Message.Contains("completed", StringComparison.OrdinalIgnoreCase)
        ).ToList();
        Assert.NotEmpty(socketCompletedEvents);
    }

    // ── Regression: full-context cast task construction ──────────────
    //
    // Guards the fix for the E2E symptom where the agent self-terminated
    // because ReadCastTask fed only the work-item title ("# This is a story")
    // to the planning materia instead of the full story body.
    //
    // ReadCastTask is private static, so these tests invoke it via reflection
    // with on-disk temp directories containing the expected artifact files.

    [Fact]
    public void ReadCastTask_FullWorkItemWithAcceptanceCriteriaAndComments_ContainsAllSections()
    {
        // Arrange: create temp directory with work-item.md, acceptance-criteria.md, comments.md.
        var contextDir = Path.Combine(Path.GetTempPath(), $"pimateria-readcasttask-{Guid.NewGuid():N}");
        Directory.CreateDirectory(contextDir);

        try
        {
            File.WriteAllText(
                Path.Combine(contextDir, "work-item.md"),
                "# This is a story\n\nImplement the feature that does the thing.\n\nIt should handle edge cases.\n"
            );
            File.WriteAllText(
                Path.Combine(contextDir, "acceptance-criteria.md"),
                "- Given the system is running\n- When the user clicks the button\n- Then the feature activates\n"
            );
            File.WriteAllText(
                Path.Combine(contextDir, "comments.md"),
                "This should use the existing infrastructure layer.\n"
            );

            var spec = new AgentRunSpec
            {
                RunId = "test-run-id",
                WorkRef = new ExternalWorkRef { Source = "Local", ExternalId = "WI-42" },
            };

            // Act: invoke ReadCastTask via reflection.
            var taskText = InvokeReadCastTask(spec, contextDir);

            // Assert: full work-item body is present (not just the title line).
            Assert.Contains("# This is a story", taskText);
            Assert.Contains("Implement the feature that does the thing.", taskText);
            Assert.Contains("It should handle edge cases.", taskText);

            // Assert: acceptance criteria section is appended.
            Assert.Contains("## Acceptance Criteria", taskText);
            Assert.Contains("Given the system is running", taskText);
            Assert.Contains("When the user clicks the button", taskText);
            Assert.Contains("Then the feature activates", taskText);

            // Assert: comments section is appended.
            Assert.Contains("## Comments", taskText);
            Assert.Contains("This should use the existing infrastructure layer.", taskText);

            // Assert: sections are separated by blank lines (double newline join).
            Assert.Contains("\n\n## Acceptance Criteria\n\n", taskText);
            Assert.Contains("\n\n## Comments\n\n", taskText);
        }
        finally
        {
            Directory.Delete(contextDir, recursive: true);
        }
    }

    [Fact]
    public void ReadCastTask_WorkItemOnly_ContainsFullBodyWithoutTitleOnly()
    {
        // Arrange: work-item.md with title + body, no acceptance-criteria.md or comments.md.
        var contextDir = Path.Combine(Path.GetTempPath(), $"pimateria-readcasttask-{Guid.NewGuid():N}");
        Directory.CreateDirectory(contextDir);

        try
        {
            File.WriteAllText(
                Path.Combine(contextDir, "work-item.md"),
                "# This is a story\n\nImplement the feature that does the thing.\n\nIt should handle edge cases.\n"
            );
            // No acceptance-criteria.md or comments.md.

            var spec = new AgentRunSpec
            {
                RunId = "test-run-id",
                WorkRef = new ExternalWorkRef { Source = "Local", ExternalId = "WI-42" },
            };

            // Act.
            var taskText = InvokeReadCastTask(spec, contextDir);

            // Assert: full body is present, not truncated to title only.
            Assert.Contains("# This is a story", taskText);
            Assert.Contains("Implement the feature that does the thing.", taskText);
            Assert.Contains("It should handle edge cases.", taskText);

            // Assert: no extra sections appended.
            Assert.DoesNotContain("## Acceptance Criteria", taskText);
            Assert.DoesNotContain("## Comments", taskText);
            Assert.DoesNotContain("Complete work item", taskText);
        }
        finally
        {
            Directory.Delete(contextDir, recursive: true);
        }
    }

    [Fact]
    public void ReadCastTask_NoWorkItemFile_ReturnsGenericFallback()
    {
        // Arrange: context directory with no work-item.md.
        var contextDir = Path.Combine(Path.GetTempPath(), $"pimateria-readcasttask-{Guid.NewGuid():N}");
        Directory.CreateDirectory(contextDir);

        try
        {
            var spec = new AgentRunSpec
            {
                RunId = "test-run-id",
                WorkRef = new ExternalWorkRef { Source = "Local", ExternalId = "WI-99" },
            };

            // Act.
            var taskText = InvokeReadCastTask(spec, contextDir);

            // Assert: generic fallback string with the external work reference.
            Assert.Equal("Complete work item WI-99.", taskText);
        }
        finally
        {
            Directory.Delete(contextDir, recursive: true);
        }
    }

    [Fact]
    public void ReadCastTask_NoWorkItemFile_FallsBackToRunIdWhenExternalIdMissing()
    {
        // Arrange: context directory with no work-item.md, spec with no ExternalId.
        var contextDir = Path.Combine(Path.GetTempPath(), $"pimateria-readcasttask-{Guid.NewGuid():N}");
        Directory.CreateDirectory(contextDir);

        try
        {
            var spec = new AgentRunSpec
            {
                RunId = "test-run-id-abc",
                WorkRef = new ExternalWorkRef { Source = "Local" },
            };

            // Act.
            var taskText = InvokeReadCastTask(spec, contextDir);

            // Assert: falls back to RunId when ExternalId is absent.
            Assert.Equal("Complete work item test-run-id-abc.", taskText);
        }
        finally
        {
            Directory.Delete(contextDir, recursive: true);
        }
    }

    /// <summary>
    /// Invoke the private static <c>ReadCastTask</c> method via reflection.
    /// </summary>
    private static string InvokeReadCastTask(AgentRunSpec spec, string contextDir)
    {
        var method = typeof(PiMateriaRuntime)
            .GetMethod("ReadCastTask", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (string)method.Invoke(null, new object?[] { spec, contextDir })!;
    }

    // ── Keepalive-stall detection ──────────────────────────────────
    //
    // The keepalive-stall detector prevents runs from hanging indefinitely
    // when pi-materia dies without reaching a valid terminal state.
    // It tracks the last runtime event (stdout line, heartbeat, etc.) and
    // fails the run with a retryable error if no event is observed within
    // max(KeepaliveStallSeconds, HeartbeatIntervalSeconds × 3).

    [Fact]
    public async Task StartAsync_KeepaliveStallDetected_RunFailsWithRetryableError()
    {
        // Overwrite the fake pi with one that accepts the prompt but goes
        // completely silent (no stdout events, no webhook events).
        WriteFakePiScript(_fakePiPath, FakePiVariant.KeepaliveStall);

        // Build a harness but don't dispose it until after the stall fires.
        var harness = await NewHarnessAsync(_fakePiPath);

        var runId = await SeedRunAsync();
        var envDir = Path.Combine(_tempRoot, runId);
        var contextDir = Path.Combine(envDir, "context");
        var repoDir = Path.Combine(envDir, "repo");
        Directory.CreateDirectory(contextDir);
        Directory.CreateDirectory(repoDir);
        await File.WriteAllTextAsync(
            Path.Combine(contextDir, "work-item.md"),
            "# Keepalive stall test\n\nSome acceptance criteria.\n"
        );

        // Use a very short stall threshold for the test:
        // HeartbeatIntervalSeconds=2, KeepaliveStallSeconds=4.
        // Effective deadline = max(4, 2×3) = 6 seconds.
        // Disable synthetic heartbeat so the stall detector can fire
        // (the fake pi goes silent after cast_start, and the synthetic
        // heartbeat would keep refreshing LastEventAt preventing stall).
        var runtime = new PiMateriaRuntime(
            _scopeFactory,
            new StaticOptionsMonitor<RuntimeOptions>(
                new RuntimeOptions
                {
                    Provider = "PiMateria",
                    PiExecutablePath = harness.FakePiPath,
                    ControllerBaseUrl = harness.BaseUrl,
                    DefaultMateriaLoadout = "Wedge",
                    HeartbeatIntervalSeconds = 2,
                    KeepaliveStallSeconds = 4,
                    DisableSyntheticHeartbeat = true,
                    PromptAcceptanceTimeoutSeconds = 4,
                    CancelGracePeriodSeconds = 3,
                }
            ),
            _provider.GetRequiredService<ILogger<PiMateriaRuntime>>()
        );

        var spec = new AgentRunSpec
        {
            RunId = runId,
            WorkRef = new ExternalWorkRef { Source = "LocalFile", ExternalId = "test-stall" },
            EnvironmentHandle = new EnvironmentHandle { RootPath = envDir },
            RepoCheckout = new RepositoryCheckout { LocalPath = repoDir, RepoKey = "widget" },
        };

        _ = await runtime.StartAsync(spec, CancellationToken.None);

        // The stall detector should fire within the effective deadline
        // (max(4, 2×3) = 6s) plus a small polling margin. We give it 15s total.
        await WaitForStateAsync(
            runId,
            s => s == RunLifecycleState.Failed,
            TimeSpan.FromSeconds(15)
        );

        var run = await _runStore.GetByIdAsync(runId, CancellationToken.None);
        Assert.NotNull(run);
        Assert.Equal(RunLifecycleState.Failed, run!.Status);

        // Verify the failure is a keepalive-stall with retryable=true.
        // Keepalive-stall uses runtime.failed_retryable (not runtime.failed)
        // so the controller can evaluate the run-level retry threshold.
        var events = await _eventStore.ListByRunIdAsync(runId, CancellationToken.None);
        var failureEvents = events.Where(e => e.EventType == RuntimeEventTypes.FailedRetryable).ToList();
        Assert.NotEmpty(failureEvents);
        var failureEvent = failureEvents.First();

        // The message should mention keepalive-stall.
        Assert.True(
            failureEvent.Message != null && failureEvent.Message.Contains("Keepalive-stall", StringComparison.OrdinalIgnoreCase),
            $"Expected failure message to mention 'Keepalive-stall'. Got: {failureEvent.Message}"
        );

        // The payload should contain the stall reason and retryable flag.
        Assert.NotNull(failureEvent.Payload);
        Assert.True(
            failureEvent.Payload!.TryGetValue("reason", out var reason) &&
            reason?.ToString() == "keepalive_stall",
            $"Expected payload reason='keepalive_stall'. Got: {string.Join(", ", failureEvent.Payload!.Select(kv => kv.Key + "=" + kv.Value))}"
        );
        Assert.True(
            failureEvent.Payload!.TryGetValue("retryable", out var retryable) &&
            retryable is true,
            $"Expected payload retryable=true. Got: {string.Join(", ", failureEvent.Payload!.Select(kv => kv.Key + "=" + kv.Value))}"
        );

        runtime.Dispose();
        await harness.DisposeAsync();
    }

    [Fact]
    public async Task StartAsync_HealthyProgression_SyntheticHeartbeatPreventsStall()
    {
        // The Idle variant accepts the prompt and posts a runtime.accepted webhook,
        // then stays alive with no further stdout events. The synthetic heartbeat
        // should keep the keepalive-stall detector from firing.
        WriteFakePiScript(_fakePiPath, FakePiVariant.Idle);

        await using var harness = await NewHarnessAsync(_fakePiPath);
        var spec = await BuildSpecAsync(harness, "Healthy progression test");

        var runtime = harness.Runtime!;
        var handle = await runtime.StartAsync(spec, CancellationToken.None);

        // Wait for the accepted webhook to be processed.
        await WaitForStateAsync(
            spec.RunId,
            s => s == RunLifecycleState.AwaitingResult || s == RunLifecycleState.AgentRunning,
            TimeSpan.FromSeconds(15)
        );

        // Let the synthetic heartbeat run for a few cycles (heartbeat interval is 2s).
        // The stall detector should NOT fire because synthetic heartbeats refresh
        // the LastEventAt timestamp.
        await Task.Delay(TimeSpan.FromSeconds(8));

        // The run should still be non-terminal (not failed from stall).
        var run = await _runStore.GetByIdAsync(spec.RunId, CancellationToken.None);
        Assert.NotNull(run);
        Assert.True(
            run!.Status == RunLifecycleState.AgentRunning ||
            run.Status == RunLifecycleState.AwaitingResult,
            $"Expected non-terminal state during healthy progression, got {run.Status}. " +
            "The synthetic heartbeat should keep the stall detector from firing."
        );

        // Verify no keepalive-stall failure events.
        var events = await _eventStore.ListByRunIdAsync(spec.RunId, CancellationToken.None);
        Assert.DoesNotContain(
            events,
            e => e.EventType == RuntimeEventTypes.Failed &&
                 e.Message != null &&
                 e.Message.Contains("Keepalive-stall", StringComparison.OrdinalIgnoreCase)
        );

        // Clean up: cancel the run.
        await runtime.CancelAsync(handle, CancellationToken.None);
        runtime.Dispose();
    }

    // ── cast_end: terminal signal for whole-cast completion ──────────

    [Fact]
    public async Task StartAsync_CastEndTerminal_RunCompletes()
    {
        // cast_end is the whole-cast terminal signal. The runtime should
        // shut down the process on cast_end, and the process exit handler
        // should synthesize a completion event from exit code 0.
        WriteFakePiScript(_fakePiPath, FakePiVariant.CastEndTerminal);

        await using var harness = await NewHarnessAsync(_fakePiPath);
        var spec = await BuildSpecAsync(harness, "cast_end terminal test");

        var runtime = harness.Runtime!;
        _ = await runtime.StartAsync(spec, CancellationToken.None);

        // The runtime should reach a terminal state (Completed from exit code 0
        // synthesis, since no webhook was posted).
        await WaitForStateAsync(
            spec.RunId,
            s => s == RunLifecycleState.Completed || s == RunLifecycleState.Failed,
            TimeSpan.FromSeconds(15)
        );

        var run = await _runStore.GetByIdAsync(spec.RunId, CancellationToken.None);
        Assert.NotNull(run);
        // Exit code 0 → no_changes_needed → Completed.
        Assert.Equal(RunLifecycleState.Completed, run!.Status);

        // Verify the cast_end status event was emitted.
        var events = await _eventStore.ListByRunIdAsync(spec.RunId, CancellationToken.None);
        var castEndEvents = events.Where(e =>
            e.EventType == RuntimeEventTypes.Status &&
            e.Message != null &&
            e.Message.Contains("Cast completed", StringComparison.OrdinalIgnoreCase)
        ).ToList();
        Assert.NotEmpty(castEndEvents);
    }

    [Fact]
    public async Task StartAsync_MultiSocketAgentEndThenCastEnd_AllSocketsComplete()
    {
        // Multi-socket cast: agent_end for each socket (non-terminal), then
        // cast_end (terminal). The runtime must NOT shut down on any agent_end,
        // but MUST shut down on cast_end. All 3 sockets should be tracked as
        // completed.
        WriteFakePiScript(_fakePiPath, FakePiVariant.MultiSocketAgentEndThenCastEnd);

        await using var harness = await NewHarnessAsync(_fakePiPath);
        var spec = await BuildSpecAsync(harness, "Multi-socket agent_end + cast_end");

        var runtime = harness.Runtime!;
        _ = await runtime.StartAsync(spec, CancellationToken.None);

        // The runtime should reach a terminal state.
        await WaitForStateAsync(
            spec.RunId,
            s => s == RunLifecycleState.Completed || s == RunLifecycleState.Failed,
            TimeSpan.FromSeconds(15)
        );

        var run = await _runStore.GetByIdAsync(spec.RunId, CancellationToken.None);
        Assert.NotNull(run);
        // Exit code 0 → no_changes_needed → Completed.
        Assert.Equal(RunLifecycleState.Completed, run!.Status);

        // Verify 3 socket completion events (one per agent_end).
        // The message format is "Socket Socket-N completed".
        var events = await _eventStore.ListByRunIdAsync(spec.RunId, CancellationToken.None);
        var socketCompletedEvents = events.Where(e =>
            e.EventType == RuntimeEventTypes.Status &&
            e.Message != null &&
            e.Message.StartsWith("Socket ", StringComparison.Ordinal) &&
            e.Message.Contains("completed", StringComparison.OrdinalIgnoreCase)
        ).ToList();
        Assert.Equal(3, socketCompletedEvents.Count);

        // Verify the cast_end status event was emitted.
        var castEndEvents = events.Where(e =>
            e.EventType == RuntimeEventTypes.Status &&
            e.Message != null &&
            e.Message.Contains("Cast completed", StringComparison.OrdinalIgnoreCase)
        ).ToList();
        Assert.NotEmpty(castEndEvents);

        // Verify no keepalive-stall failure (the runtime stayed alive across all sockets).
        Assert.DoesNotContain(
            events,
            e => e.EventType == RuntimeEventTypes.Failed &&
                 e.Message != null &&
                 e.Message.Contains("Keepalive-stall", StringComparison.OrdinalIgnoreCase)
        );
    }

    // ── Harness / fixture helpers ────────────────────────────────────

    /// <summary>
    /// Builds an <see cref="AgentRunSpec"/> with real on-disk context (a
    /// <c>work-item.md</c> with a title) and repo directory, plus a controller
    /// base URL pointed at a real HTTP listener that ingests events.
    /// </summary>
    private async Task<TestHarness> NewHarnessAsync(string fakePiPath)
    {
        // Allocate a real ephemeral port for the listener.
        using var portFinder = new TcpListener(IPAddress.Loopback, 0);
        portFinder.Start();
        var port = ((IPEndPoint)portFinder.LocalEndpoint).Port;
        portFinder.Stop();

        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        var baseUrl = $"http://127.0.0.1:{port}";

        var listenerCts = new CancellationTokenSource();
        var listenerTask = RunListenerAsync(listener, listenerCts.Token);

        // Reconfigure the runtime options to point at the fake pi and listener.
        // We swap the singleton by building a fresh provider is heavy; instead we
        // construct the runtime directly in tests. NewHarnessAsync returns the
        // pieces; each test resolves the runtime via a factory that uses these
        // options.
        return new TestHarness(fakePiPath, baseUrl, listener, listenerCts, listenerTask, _tempRoot);
    }

    private async Task<AgentRunSpec> BuildSpecAsync(TestHarness harness, string title)
    {
        var runId = await SeedRunAsync();

        var envDir = Path.Combine(_tempRoot, runId);
        var contextDir = Path.Combine(envDir, "context");
        var repoDir = Path.Combine(envDir, "repo");
        Directory.CreateDirectory(contextDir);
        Directory.CreateDirectory(repoDir);
        await File.WriteAllTextAsync(
            Path.Combine(contextDir, "work-item.md"),
            $"# {title}\n\nSome acceptance criteria.\n"
        );

        // Swap the runtime's options to point at the fake pi + listener.
        // We rebuild a runtime bound to these options so each test is isolated.
        harness.Runtime = new PiMateriaRuntime(
            _scopeFactory,
            new StaticOptionsMonitor<RuntimeOptions>(
                new RuntimeOptions
                {
                    Provider = "PiMateria",
                    PiExecutablePath = harness.FakePiPath,
                    ControllerBaseUrl = harness.BaseUrl,
                    DefaultMateriaLoadout = "Wedge",
                    HeartbeatIntervalSeconds = 2,
                    PromptAcceptanceTimeoutSeconds = 4,
                    CancelGracePeriodSeconds = 3,
                }
            ),
            _provider.GetRequiredService<ILogger<PiMateriaRuntime>>()
        );

        return new AgentRunSpec
        {
            RunId = runId,
            WorkRef = new ExternalWorkRef { Source = "LocalFile", ExternalId = "test-1" },
            EnvironmentHandle = new EnvironmentHandle { RootPath = envDir },
            RepoCheckout = new RepositoryCheckout { LocalPath = repoDir, RepoKey = "widget" },
        };
    }

    private async Task<string> SeedRunAsync()
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var lifecycle = scope.ServiceProvider.GetRequiredService<IRunLifecycleService>();
        var workItemStore = scope.ServiceProvider.GetRequiredService<IWorkItemStore>();

        var wi = await workItemStore.CreateAsync(
            new CreateWorkItemRequest { RepoKey = "widget", Title = "Test item" },
            CancellationToken.None
        );
        var run = await lifecycle.CreateRunForWorkItemAsync(
            wi.Id,
            "test-worker",
            CancellationToken.None
        );

        foreach (
            var state in new[]
            {
                RunLifecycleState.EnvironmentProvisioning,
                RunLifecycleState.EnvironmentReady,
                RunLifecycleState.RepositoryCloning,
                RunLifecycleState.RepositoryReady,
                RunLifecycleState.ContextInjected,
                RunLifecycleState.AgentStarting,
            }
        )
        {
            await lifecycle.TransitionAsync(run.RunId, state, CancellationToken.None);
        }

        return run.RunId;
    }

    private async Task WaitForStateAsync(
        string runId,
        Func<RunLifecycleState, bool> predicate,
        TimeSpan timeout
    )
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var run = await _runStore.GetByIdAsync(runId, CancellationToken.None);
            if (run is not null && predicate(run.Status))
            {
                return;
            }

            await Task.Delay(100);
        }

        var final = await _runStore.GetByIdAsync(runId, CancellationToken.None);
        DumpDiagnostics(runId, final);
        Assert.Fail(
            $"Run '{runId}' did not reach target state within {timeout.TotalSeconds}s. "
                + $"Final state: {final?.Status.ToString() ?? "null"}."
        );
    }

    /// <summary>
    /// Dump run state, recorded event types, and the fake-pi diagnostic log to
    /// the test output for debugging.
    /// </summary>
    private void DumpDiagnostics(string runId, AgentRunHandle? final)
    {
        try
        {
            _output.WriteLine($"--- DIAGNOSTICS for {runId} ---");
            _output.WriteLine($"final state: {final?.Status.ToString() ?? "null"}");
            _output.WriteLine($"error: {final?.Error ?? "(none)"}");

            var events = _eventStore.Snapshot(runId);
            _output.WriteLine(
                $"events ({events.Count}): " + string.Join(", ", events.Select(e => e.EventType))
            );

            foreach (
                var e in events.Where(e =>
                    e.EventType.StartsWith("runtime.", StringComparison.Ordinal)
                )
            )
            {
                _output.WriteLine(
                    $"  [{e.EventType}] msg={e.Message ?? "(none)"} "
                        + "payload="
                        + (
                            e.Payload is null
                                ? "(none)"
                                : string.Join(",", e.Payload.Select(kv => kv.Key + "=" + kv.Value))
                        )
                );
            }

            var envDir = Path.Combine(_tempRoot, runId);
            var diag = Path.Combine(envDir, "context", "fake-pi.diag.log");
            if (File.Exists(diag))
            {
                _output.WriteLine("--- fake-pi.diag.log ---");
                foreach (var line in File.ReadAllLines(diag))
                {
                    _output.WriteLine("  " + line);
                }
            }
            else
            {
                _output.WriteLine("(no fake-pi.diag.log at " + diag + ")");
            }
        }
        catch
        {
            // best-effort
        }
    }

    /// <summary>
    /// Drive the HTTP listener: every POST is parsed into a
    /// <see cref="RuntimeEvent"/> and ingested via the real lifecycle service.
    /// GET requests return a 200 health probe.
    /// </summary>
    private async Task RunListenerAsync(HttpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await listener.GetContextAsync().WaitAsync(ct);
            }
            catch (HttpListenerException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }

            // Handle on a background task so one slow request cannot block others.
            _ = Task.Run(() => HandleListenerRequestAsync(ctx, ct), CancellationToken.None);
        }
    }

    private async Task HandleListenerRequestAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        try
        {
            if (ctx.Request.HttpMethod == "GET")
            {
                ctx.Response.StatusCode = 200;
                await using var w = new StreamWriter(ctx.Response.OutputStream);
                await w.WriteAsync("{\"ok\":true}");
                return;
            }

            if (ctx.Request.HttpMethod != "POST")
            {
                ctx.Response.StatusCode = 404;
                return;
            }

            using var reader = new StreamReader(ctx.Request.InputStream);
            var body = await reader.ReadToEndAsync(ct);
            var evt = ParseEvent(body, ctx.Request.Url?.AbsolutePath ?? string.Empty);
            if (evt is null)
            {
                ctx.Response.StatusCode = 400;
                return;
            }

            await using var scope = _scopeFactory.CreateAsyncScope();
            var lifecycle = scope.ServiceProvider.GetRequiredService<IRunLifecycleService>();
            try
            {
                await lifecycle.IngestRuntimeEventAsync(evt, ct);
                ctx.Response.StatusCode = 200;
            }
            catch (InvalidOperationException)
            {
                // Duplicate/terminal — match the real endpoint's conflict semantics.
                ctx.Response.StatusCode = 409;
            }
        }
        catch
        {
            try
            {
                ctx.Response.StatusCode = 500;
            }
            catch
            { /* best-effort */
            }
        }
        finally
        {
            try
            {
                ctx.Response.Close();
            }
            catch
            { /* best-effort */
            }
        }
    }

    /// <summary>
    /// Parse a posted JSON body into a <see cref="RuntimeEvent"/>, deriving
    /// <c>runId</c> from the route (<c>/runs/{runId}/events</c>).
    /// </summary>
    private static RuntimeEvent? ParseEvent(string body, string path)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(body);
            root = doc.RootElement.Clone();
        }
        catch
        {
            return null;
        }

        // runId from route: /runs/<runId>/events
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var runId = segments.Length >= 2 ? segments[1] : null;

        if (!root.TryGetProperty("eventId", out _))
        {
            return null;
        }

        return new RuntimeEvent
        {
            EventId = root.GetProperty("eventId").GetString() ?? Guid.NewGuid().ToString("N"),
            RunId = runId ?? string.Empty,
            EventType = root.TryGetProperty("eventType", out var t) ? t.GetString() ?? "" : "",
            RuntimeRunId = root.TryGetProperty("runtimeRunId", out var rr) ? rr.GetString() : null,
            Message = root.TryGetProperty("message", out var m) ? m.GetString() : null,
            OccurredAt =
                root.TryGetProperty("occurredAt", out var o)
                && DateTimeOffset.TryParse(o.GetString(), out var occ)
                    ? occ
                    : DateTimeOffset.UtcNow,
            Payload = root.TryGetProperty("payload", out var pl) ? ToPayloadDictionary(pl) : null,
        };
    }

    private static Dictionary<string, object?>? ToPayloadDictionary(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var dict = new Dictionary<string, object?>();
        foreach (var prop in element.EnumerateObject())
        {
            dict[prop.Name] = prop.Value.ValueKind switch
            {
                JsonValueKind.String => prop.Value.GetString(),
                JsonValueKind.Number => prop.Value.TryGetInt64(out var l)
                    ? (object)l
                    : prop.Value.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                _ => prop.Value.GetRawText(),
            };
        }

        return dict;
    }

    // ── Fake pi script generator ─────────────────────────────────────

    private enum FakePiVariant
    {
        HappyPr,
        Crash,
        Idle,
        AgentEnd,
        AgentEndTerminalRegression,
        UnrecognizedType,
        UnrecognizedTypeInteractive,
        MultiTurnAgentSocket,
        SingleTurnAgentSocket,
        MultiTurnInteractive,
        AllRecognizedTypes,
        MultiSocketAgentEnd,
        BiggsLoadoutSocket3Divergence,
        KeepaliveStall,
        TelemetryEventsIgnored,
        CastEndTerminal,
        MultiSocketAgentEndThenCastEnd,
    }

    /// <summary>
    /// Write a Python script that masquerades as the pi RPC process. The runtime
    /// invokes it with <c>--mode rpc --no-session</c> (ignored) and drives it via
    /// stdin JSONL. Variants:
    /// <list type="bullet">
    ///   <item><c>HappyPr</c> — on prompt, POSTs accepted/status/completed
    ///       (pull_request_opened) and stays alive until aborted/killed.</item>
    ///   <item><c>Crash</c> — exits 1 immediately without posting anything.</item>
    ///   <item><c>Idle</c> — acknowledges the prompt and stays alive forever
    ///       (for cancel testing).</item>
    /// </list>
    /// </summary>
    private static void WriteFakePiScript(string path, FakePiVariant variant)
    {
        var body = variant switch
        {
            FakePiVariant.HappyPr => FakePiScripts.HappyPr,
            FakePiVariant.Crash => FakePiScripts.Crash,
            FakePiVariant.Idle => FakePiScripts.Idle,
            FakePiVariant.AgentEnd => FakePiScripts.AgentEnd,
            FakePiVariant.AgentEndTerminalRegression => FakePiScripts.AgentEndTerminalRegression,
            FakePiVariant.UnrecognizedType => FakePiScripts.UnrecognizedType,
            FakePiVariant.UnrecognizedTypeInteractive => FakePiScripts.UnrecognizedTypeInteractive,
            FakePiVariant.MultiTurnAgentSocket => FakePiScripts.MultiTurnAgentSocket,
            FakePiVariant.SingleTurnAgentSocket => FakePiScripts.SingleTurnAgentSocket,
            FakePiVariant.MultiTurnInteractive => FakePiScripts.MultiTurnInteractive,
            FakePiVariant.AllRecognizedTypes => FakePiScripts.AllRecognizedTypes,
            FakePiVariant.MultiSocketAgentEnd => FakePiScripts.MultiSocketAgentEnd,
            FakePiVariant.BiggsLoadoutSocket3Divergence => FakePiScripts.BiggsLoadoutSocket3Divergence,
            FakePiVariant.KeepaliveStall => FakePiScripts.KeepaliveStall,
            FakePiVariant.TelemetryEventsIgnored => FakePiScripts.TelemetryEventsIgnored,
            FakePiVariant.CastEndTerminal => FakePiScripts.CastEndTerminal,
            FakePiVariant.MultiSocketAgentEndThenCastEnd => FakePiScripts.MultiSocketAgentEndThenCastEnd,
            _ => FakePiScripts.HappyPr,
        };

        File.WriteAllText(path, body);

        // Make it directly executable so the runtime can launch it via FileName.
        try
        {
            // chmod +x
            var info = new System.Diagnostics.ProcessStartInfo("chmod", ["+x", path])
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            System.Diagnostics.Process.Start(info)?.WaitForExit(2000);
        }
        catch
        {
            // chmod is best-effort; the Python shebang + python3 fallback still works.
        }
    }

    private static class FakePiScripts
    {
        public const string Header =
            "#!/usr/bin/env python3\n"
            + "\"\"\"Fake pi RPC process for PiMateriaRuntimeTests.\"\"\"\n"
            + "import json, os, sys, time, urllib.request\n\n"
            + "EVENT_URL = os.environ.get('CONTROLLER_EVENT_URL', '')\n"
            + "RUN_ID = os.environ.get('CONTROLLER_RUN_ID', '')\n"
            + "_CTX = os.environ.get('CONTROLLER_CONTEXT_DIR', '')\n"
            + "_DIAG = os.path.join(_CTX, 'fake-pi.diag.log') if _CTX else None\n\n"
            + "def diag(msg):\n"
            + "    if _DIAG:\n"
            + "        try: open(_DIAG, 'a').write(msg + '\\n')\n"
            + "        except Exception: pass\n"
            + "    sys.stderr.write(msg + '\\n'); sys.stderr.flush()\n\n"
            + "def send_response(obj):\n"
            + "    sys.stdout.write(json.dumps(obj) + '\\n')\n"
            + "    sys.stdout.flush()\n\n"
            + "def post_event(payload):\n"
            + "    if not EVENT_URL:\n"
            + "        return\n"
            + "    data = json.dumps(payload).encode('utf-8')\n"
            + "    req = urllib.request.Request(EVENT_URL, data=data, headers={'Content-Type': 'application/json'})\n"
            + "    try:\n"
            + "        urllib.request.urlopen(req, timeout=5).read()\n"
            + "    except Exception as e:\n"
            + "        diag('post failed: ' + str(e))\n\n";

        public const string HappyPr =
            Header
            + """
def main():
    while True:
        line = sys.stdin.readline()
        if not line:
            return
        try:
            msg = json.loads(line)
        except Exception:
            continue
        if msg.get('type') == 'prompt':
            send_response({'type': 'response', 'command': 'prompt', 'success': True})
            post_event({'eventId': 'fp-accepted', 'eventType': 'runtime.accepted', 'occurredAt': '2026-06-23T00:00:00Z', 'severity': 'info', 'message': 'accepted'})
            time.sleep(0.05)
            post_event({'eventId': 'fp-status', 'eventType': 'runtime.status', 'message': 'working'})
            time.sleep(0.05)
            post_event({'eventId': 'fp-completed', 'eventType': 'runtime.completed', 'occurredAt': '2026-06-23T00:00:01Z', 'severity': 'info', 'message': 'done', 'payload': {'outcome': 'pull_request_opened', 'summary': 'fake PR', 'branchName': 'agent/test', 'pullRequestUrl': 'http://example.test/pr/1'}})
            # Stay alive until the controller shuts us down (mirrors real pi RPC).
            continue
        if msg.get('type') == 'abort':
            return

main()
""";

        public const string Crash =
            Header
            + """
def main():
    # Read the prompt so the pipe is drained, then crash without any event.
    line = sys.stdin.readline()
    sys.exit(1)

main()
""";

        public const string Idle =
            Header
            + """
def main():
    while True:
        line = sys.stdin.readline()
        if not line:
            return
        try:
            msg = json.loads(line)
        except Exception:
            continue
        if msg.get('type') == 'prompt':
            send_response({'type': 'response', 'command': 'prompt', 'success': True})
            post_event({'eventId': 'fp-accepted', 'eventType': 'runtime.accepted', 'occurredAt': '2026-06-23T00:00:00Z', 'severity': 'info', 'message': 'accepted'})
            # Then idle forever — used to exercise cancel.
            continue
        if msg.get('type') == 'abort':
            return

main()
""";

        /// <summary>
        /// Acknowledges the prompt, emits <c>agent_end</c> on stdout (simulating
        /// a single-turn agent completing its cast), then exits cleanly. No
        /// webhook events are posted — agent_end is per-socket and non-terminal,
        /// so the process exit handler synthesizes a terminal event from exit code 0.
        /// </summary>
        public const string AgentEnd =
            Header
            + """
def main():
    while True:
        line = sys.stdin.readline()
        if not line:
            return
        try:
            msg = json.loads(line)
        except Exception:
            continue
        if msg.get('type') == 'prompt':
            send_response({'type': 'response', 'command': 'prompt', 'success': True})
            # Emit agent_end on stdout — per-socket completion (non-terminal).
            send_response({'type': 'agent_end', 'messages': []})
            # Exit cleanly — the process exit handler synthesizes a terminal event from exit code 0.
            time.sleep(0.1)
            return
        if msg.get('type') == 'abort':
            return

main()
""";

        /// <summary>
        /// Regression E2E: exercises the multi-socket cast path that stalled in the
        /// original agent-controller E2E run. Emits the full cast lifecycle under the
        /// agent-controller preset:
        /// <c>cast_start</c> → <c>materia_start</c> → <c>materia_end</c> → <c>agent_end</c>.
        /// No webhook events are posted — agent_end is per-socket and non-terminal;
        /// the process exit handler synthesizes a terminal event from exit code 0.
        ///
        /// This guards the regression where the first agent_end (from one socket)
        /// was misread as overall completion, orphaning downstream sockets.
        /// </summary>
        public const string AgentEndTerminalRegression =
            Header
            + """
def main():
    while True:
        line = sys.stdin.readline()
        if not line:
            return
        try:
            msg = json.loads(line)
        except Exception:
            continue
        if msg.get('type') == 'prompt':
            send_response({'type': 'response', 'command': 'prompt', 'success': True})
            # Emit the full cast lifecycle that the original E2E went through.
            # cast_start with agent-controller preset and single-turn agent socket.
            send_response({
                'type': 'cast_start',
                'castId': 'regression-agent-end-terminal',
                'eventing': {'preset': 'agent-controller'},
                'sockets': [
                    {'socketName': 'Socket-1', 'type': 'utility', 'materiaName': 'Detect-VCS', 'multiTurn': False},
                    {'socketName': 'Socket-4', 'type': 'agent', 'materiaName': 'Builda', 'multiTurn': False}
                ]
            })
            # materia_start — the agent materia begins its turn.
            send_response({'type': 'materia_start', 'materiaName': 'Builda', 'socketName': 'Socket-4'})
            # materia_end — the agent materia completes its turn.
            send_response({'type': 'materia_end', 'materiaName': 'Builda', 'socketName': 'Socket-4'})
            # agent_end — per-socket completion (non-terminal). The original E2E
            # aborted mid-flight because this was misread as overall completion.
            send_response({'type': 'agent_end', 'messages': []})
            # Exit cleanly — the process exit handler synthesizes a terminal event from exit code 0.
            time.sleep(0.1)
            return
        if msg.get('type') == 'abort':
            return

main()
""";

        /// <summary>
        /// Acknowledges the prompt, emits cast_start with agent-controller preset,
        /// then emits an unrecognized stdout event type (e.g. <c>__unknown_event__</c>).
        /// Under agent-controller eventing this should fail the run with a
        /// contract-drift error instead of stalling.
        /// </summary>
        public const string UnrecognizedType =
            Header
            + """
def main():
    while True:
        line = sys.stdin.readline()
        if not line:
            return
        try:
            msg = json.loads(line)
        except Exception:
            continue
        if msg.get('type') == 'prompt':
            send_response({'type': 'response', 'command': 'prompt', 'success': True})
            # Emit cast_start with agent-controller preset so the runtime knows the eventing mode.
            send_response({
                'type': 'cast_start',
                'castId': 'test-cast-unrecognized',
                'eventing': {'preset': 'agent-controller'},
                'sockets': [
                    {'socketName': 'Socket-1', 'type': 'utility', 'materiaName': 'Detect-VCS', 'multiTurn': False}
                ]
            })
            # Emit an unrecognized event type — should fail the run under agent-controller.
            send_response({'type': '__unknown_event__', 'data': 'test'})
            # Stay alive so the monitor can detect the unrecognized type flag.
            # The monitor polls every 1s, so 3s gives it multiple chances.
            time.sleep(3)
            return
        if msg.get('type') == 'abort':
            return

main()
""";

        /// <summary>
        /// Acknowledges the prompt, emits cast_start with interactive preset,
        /// then emits an unrecognized stdout event type. Under interactive eventing
        /// the runtime should warn-and-continue (not fail the run), because a human
        /// is present to handle the situation. Posts runtime.completed webhook so
        /// the run reaches a terminal state.
        /// </summary>
        public const string UnrecognizedTypeInteractive =
            Header
            + """
def main():
    while True:
        line = sys.stdin.readline()
        if not line:
            return
        try:
            msg = json.loads(line)
        except Exception:
            continue
        if msg.get('type') == 'prompt':
            send_response({'type': 'response', 'command': 'prompt', 'success': True})
            # Emit cast_start with interactive preset — unrecognized types should NOT fail.
            send_response({
                'type': 'cast_start',
                'castId': 'test-cast-interactive-unrecognized',
                'eventing': {'preset': 'interactive'},
                'sockets': [
                    {'socketName': 'Socket-1', 'type': 'utility', 'materiaName': 'Detect-VCS', 'multiTurn': False}
                ]
            })
            # Emit an unrecognized event type — should warn but NOT fail under interactive.
            send_response({'type': '__unknown_event__', 'data': 'test'})
            # Post a completed webhook so the run reaches a terminal state.
            time.sleep(0.05)
            post_event({'eventId': 'fp-completed', 'eventType': 'runtime.completed', 'occurredAt': '2026-06-23T00:00:01Z', 'severity': 'info', 'message': 'done', 'payload': {'outcome': 'no_changes_needed', 'summary': 'interactive warn-and-continue'}})
            # Stay alive until the controller shuts us down.
            continue
        if msg.get('type') == 'abort':
            return

main()
""";

        /// <summary>
        /// Acknowledges the prompt, then emits a <c>cast_start</c> stdout event
        /// with a multiTurn agent socket. Under agent-controller eventing this
        /// should fail the run with a multiTurn-agent-socket error before any
        /// agent turn fires.
        /// </summary>
        public const string MultiTurnAgentSocket =
            Header
            + """
def main():
    while True:
        line = sys.stdin.readline()
        if not line:
            return
        try:
            msg = json.loads(line)
        except Exception:
            continue
        if msg.get('type') == 'prompt':
            send_response({'type': 'response', 'command': 'prompt', 'success': True})
            # Emit cast_start with a multiTurn agent socket — should fail the run under agent-controller.
            send_response({
                'type': 'cast_start',
                'castId': 'test-cast',
                'eventing': {'preset': 'agent-controller'},
                'sockets': [
                    {'socketName': 'Socket-1', 'type': 'utility', 'materiaName': 'Detect-VCS', 'multiTurn': False},
                    {'socketName': 'Socket-3', 'type': 'agent', 'materiaName': 'Interactive-Plan', 'multiTurn': True},
                    {'socketName': 'Socket-4', 'type': 'agent', 'materiaName': 'Build', 'multiTurn': False}
                ]
            })
            # Stay alive so the monitor can detect the multiTurn flag.
            time.sleep(3)
            return
        if msg.get('type') == 'abort':
            return

main()
""";

        /// <summary>
        /// Acknowledges the prompt, emits a <c>cast_start</c> with only single-turn
        /// agent sockets under the <c>agent-controller</c> preset, then posts a
        /// <c>runtime.completed</c> webhook. The runtime should allow this through
        /// and the run should reach a terminal state.
        /// </summary>
        public const string SingleTurnAgentSocket =
            Header
            + """
def main():
    while True:
        line = sys.stdin.readline()
        if not line:
            return
        try:
            msg = json.loads(line)
        except Exception:
            continue
        if msg.get('type') == 'prompt':
            send_response({'type': 'response', 'command': 'prompt', 'success': True})
            # Emit cast_start with only single-turn agent sockets — should be allowed.
            send_response({
                'type': 'cast_start',
                'castId': 'test-cast-single',
                'eventing': {'preset': 'agent-controller'},
                'sockets': [
                    {'socketName': 'Socket-1', 'type': 'utility', 'materiaName': 'Detect-VCS', 'multiTurn': False},
                    {'socketName': 'Socket-3', 'type': 'agent', 'materiaName': 'Auto-Plan', 'multiTurn': False},
                    {'socketName': 'Socket-4', 'type': 'agent', 'materiaName': 'Build', 'multiTurn': False}
                ]
            })
            time.sleep(0.05)
            post_event({'eventId': 'fp-completed', 'eventType': 'runtime.completed', 'occurredAt': '2026-06-23T00:00:01Z', 'severity': 'info', 'message': 'done', 'payload': {'outcome': 'no_changes_needed', 'summary': 'all single-turn'}})
            # Stay alive until the controller shuts us down.
            continue
        if msg.get('type') == 'abort':
            return

main()
""";

        /// <summary>
        /// Acknowledges the prompt, emits a <c>cast_start</c> with a multiTurn agent
        /// socket under a non-agent-controller preset ("interactive"). Then posts a
        /// <c>runtime.completed</c> webhook. The runtime should allow this through
        /// because the fail-fast guard only applies to the agent-controller preset.
        /// </summary>
        public const string MultiTurnInteractive =
            Header
            + """
def main():
    while True:
        line = sys.stdin.readline()
        if not line:
            return
        try:
            msg = json.loads(line)
        except Exception:
            continue
        if msg.get('type') == 'prompt':
            send_response({'type': 'response', 'command': 'prompt', 'success': True})
            # Emit cast_start with a multiTurn agent socket under interactive preset — should be allowed.
            send_response({
                'type': 'cast_start',
                'castId': 'test-cast-interactive',
                'eventing': {'preset': 'interactive'},
                'sockets': [
                    {'socketName': 'Socket-1', 'type': 'utility', 'materiaName': 'Detect-VCS', 'multiTurn': False},
                    {'socketName': 'Socket-3', 'type': 'agent', 'materiaName': 'Interactive-Plan', 'multiTurn': True},
                    {'socketName': 'Socket-4', 'type': 'agent', 'materiaName': 'Build', 'multiTurn': False}
                ]
            })
            time.sleep(0.05)
            post_event({'eventId': 'fp-completed', 'eventType': 'runtime.completed', 'occurredAt': '2026-06-23T00:00:01Z', 'severity': 'info', 'message': 'done', 'payload': {'outcome': 'no_changes_needed', 'summary': 'interactive multiTurn'}})
            # Stay alive until the controller shuts us down.
            continue
        if msg.get('type') == 'abort':
            return

main()
""";

        /// <summary>
        /// Emits ALL recognized stdout event types from the contract in sequence,
        /// then posts a <c>runtime.completed</c> webhook. This is the conformance
        /// test: the runtime must recognize every type and NOT emit any
        /// unrecognized-type warnings or fail the run.
        /// </summary>
        public const string AllRecognizedTypes =
            Header
            + """
def main():
    while True:
        line = sys.stdin.readline()
        if not line:
            return
        try:
            msg = json.loads(line)
        except Exception:
            continue
        if msg.get('type') == 'prompt':
            send_response({'type': 'response', 'command': 'prompt', 'success': True})
            # Emit all recognized stdout event types from the contract.
            # The runtime must handle every one without triggering unrecognized-type.
            send_response({'type': 'cast_start', 'castId': 'conformance-cast', 'eventing': {'preset': 'agent-controller'}, 'sockets': [{'socketName': 'Socket-1', 'type': 'utility', 'materiaName': 'Detect-VCS', 'multiTurn': False}]})
            send_response({'type': 'materia_start', 'materiaName': 'Detect-VCS', 'socketName': 'Socket-1'})
            send_response({'type': 'materia_end', 'materiaName': 'Detect-VCS', 'socketName': 'Socket-1'})
            send_response({'type': 'cast_end', 'castId': 'conformance-cast'})
            send_response({'type': 'agent_end', 'messages': []})
            # Post a completed webhook so the run reaches a terminal state.
            time.sleep(0.05)
            post_event({'eventId': 'fp-completed', 'eventType': 'runtime.completed', 'occurredAt': '2026-06-23T00:00:01Z', 'severity': 'info', 'message': 'done', 'payload': {'outcome': 'no_changes_needed', 'summary': 'all types recognized'}})
            # Stay alive until the controller shuts us down.
            continue
        if msg.get('type') == 'abort':
            return

main()
""";

        /// <summary>
        /// Multi-socket agent_end with socketName field.
        /// Emits cast_start with 3 agent sockets, then agent_end for each
        /// with socketName, then exits cleanly. The runtime should emit
        /// "Socket Socket-N completed" status events for each agent_end
        /// and NOT transition to a terminal state.
        /// </summary>
        public const string MultiSocketAgentEnd =
            Header
            + """
def main():
    while True:
        line = sys.stdin.readline()
        if not line:
            return
        try:
            msg = json.loads(line)
        except Exception:
            continue
        if msg.get('type') == 'prompt':
            send_response({'type': 'response', 'command': 'prompt', 'success': True})
            # cast_start with 3 agent sockets.
            send_response({
                'type': 'cast_start',
                'castId': 'multi-socket-agent-end',
                'eventing': {'preset': 'agent-controller'},
                'sockets': [
                    {'socketName': 'Socket-3', 'type': 'agent', 'materiaName': 'Interactive-Plani', 'multiTurn': False},
                    {'socketName': 'Socket-4', 'type': 'agent', 'materiaName': 'Builda', 'multiTurn': False},
                    {'socketName': 'Socket-5', 'type': 'agent', 'materiaName': 'Auto-Evala', 'multiTurn': False}
                ]
            })
            # agent_end for Socket-3 (Interactive-Plani) — per-socket, non-terminal.
            send_response({'type': 'agent_end', 'socketName': 'Socket-3', 'messages': []})
            # agent_end for Socket-4 (Builda) — per-socket, non-terminal.
            send_response({'type': 'agent_end', 'socketName': 'Socket-4', 'messages': []})
            # agent_end for Socket-5 (Auto-Evala) — per-socket, non-terminal.
            send_response({'type': 'agent_end', 'socketName': 'Socket-5', 'messages': []})
            # Exit cleanly — the process exit handler synthesizes a terminal event.
            time.sleep(0.1)
            return
        if msg.get('type') == 'abort':
            return

main()
""";

        /// <summary>
        /// Regression test for the Biggs loadout Socket-3 materia divergence incident.
        ///
        /// The "Biggs" loadout (user copy of default:full-auto) had Socket-3 bound
        /// to Interactive-Plani (multiTurn=true) instead of Auto-Plan (multiTurn=false).
        /// This caused the agent-controller run to stall because the controller never
        /// sends /materia continue for multiTurn materias.
        ///
        /// This test simulates the exact cast_start event shape from the Biggs loadout
        /// and asserts the runtime fails fast with a clear multiTurn error.
        ///
        /// See: docs/investigations/socket-3-materia-divergence.md
        /// </summary>
        public const string BiggsLoadoutSocket3Divergence =
            Header
            + """
def main():
    while True:
        line = sys.stdin.readline()
        if not line:
            return
        try:
            msg = json.loads(line)
        except Exception:
            continue
        if msg.get('type') == 'prompt':
            send_response({'type': 'response', 'command': 'prompt', 'success': True})
            # Emit cast_start matching the Biggs loadout's resolved socket configuration.
            # Socket-3 is bound to Interactive-Plani (multiTurn=true) — the divergence.
            # The runtime must detect this and fail the run under agent-controller eventing.
            send_response({
                'type': 'cast_start',
                'castId': 'biggs-divergence-regression',
                'eventing': {'preset': 'agent-controller'},
                'loadout': 'Biggs',
                'loadoutId': 'user:rude-copy:15d29129-5e29-4bb2-8562-0356fc3ebc2f',
                'sockets': [
                    {'socketName': 'Socket-1', 'type': 'utility', 'materiaName': 'Ignore-Artifacts', 'multiTurn': False},
                    {'socketName': 'Socket-2', 'type': 'utility', 'materiaName': 'Blackbelt-Bootstrap', 'multiTurn': False},
                    {'socketName': 'Socket-3', 'type': 'agent', 'materiaName': 'Interactive-Plani', 'multiTurn': True},
                    {'socketName': 'Socket-4', 'type': 'agent', 'materiaName': 'Builda', 'multiTurn': False},
                    {'socketName': 'Socket-5', 'type': 'agent', 'materiaName': 'Auto-Evala', 'multiTurn': False},
                    {'socketName': 'Socket-6', 'type': 'utility', 'materiaName': 'Blackbelt-Maintain', 'multiTurn': False},
                    {'socketName': 'Socket-7', 'type': 'agent', 'materiaName': 'Narrata', 'multiTurn': False},
                    {'socketName': 'Socket-8', 'type': 'utility', 'materiaName': 'Commit-Sigil', 'multiTurn': False}
                ]
            })
            # Stay alive so the monitor can detect the multiTurn flag.
            time.sleep(3)
            return
        if msg.get('type') == 'abort':
            return

main()
""";

        /// <summary>
        /// Simulates an orphaned AwaitingResult: accepts the prompt, emits cast_start,
        /// then goes completely silent (no stdout events, no webhook events).
        /// The keepalive-stall detector should fire and fail the run.
        /// Stays alive long enough for the stall detector to trigger.
        /// </summary>
        public const string KeepaliveStall =
            Header
            + """
def main():
    while True:
        line = sys.stdin.readline()
        if not line:
            return
        try:
            msg = json.loads(line)
        except Exception:
            continue
        if msg.get('type') == 'prompt':
            send_response({'type': 'response', 'command': 'prompt', 'success': True})
            # Emit cast_start so the runtime has a castId for diagnostics.
            send_response({
                'type': 'cast_start',
                'castId': 'stall-test-cast',
                'eventing': {'preset': 'agent-controller'},
                'sockets': [
                    {'socketName': 'Socket-4', 'type': 'agent', 'materiaName': 'Builda', 'multiTurn': False}
                ]
            })
            # Then go completely silent — no stdout events, no webhook events.
            # The keepalive-stall detector should fire and fail the run.
            # Stay alive long enough for the stall detector to trigger (60s).
            diag('stall-test: going silent...')
            for _ in range(600):
                if not sys.stdin.readline():
                    break
                time.sleep(0.1)
            return
        if msg.get('type') == 'abort':
            return

main()
""";

        /// <summary>
        /// Emits all known telemetry-only pi-core event types followed by
        /// lifecycle events (cast_start, agent_end, etc.), then posts a
        /// runtime.completed webhook. The telemetry types should be silently
        /// ignored (no unrecognized-type warnings) and the lifecycle events
        /// should route correctly. The run should complete successfully.
        /// </summary>
        public const string TelemetryEventsIgnored =
            Header
            + """
def main():
    while True:
        line = sys.stdin.readline()
        if not line:
            return
        try:
            msg = json.loads(line)
        except Exception:
            continue
        if msg.get('type') == 'prompt':
            send_response({'type': 'response', 'command': 'prompt', 'success': True})
            # Emit all telemetry-only pi-core event types from the ignore list.
            # These should be silently ignored — no unrecognized-type warnings.
            send_response({'type': 'extension_ui_request', 'id': 'test-1', 'method': 'setWidget', 'widgetKey': 'materia', 'widgetLines': ['test'], 'widgetPlacement': 'belowEditor'})
            send_response({'type': 'session_info_changed', 'name': 'test-session'})
            send_response({'type': 'message_start', 'message': {'role': 'custom', 'customType': 'pi-materia'}})
            send_response({'type': 'message_end', 'message': {'role': 'assistant'}})
            send_response({'type': 'message_update', 'assistantMessageEvent': {'type': 'thinking_delta'}})
            send_response({'type': 'agent_start'})
            send_response({'type': 'turn_start'})
            send_response({'type': 'turn_end', 'message': {'role': 'assistant'}})
            send_response({'type': 'tool_execution_start', 'toolCallId': 'test-tool-1', 'toolName': 'ls', 'args': {'path': '.'}})
            send_response({'type': 'tool_execution_end', 'toolCallId': 'test-tool-1', 'toolName': 'ls', 'result': {'content': []}, 'isError': False})
            # Now emit real lifecycle events that MUST still route correctly.
            send_response({'type': 'cast_start', 'castId': 'telemetry-ignore-test', 'eventing': {'preset': 'agent-controller'}, 'sockets': [{'socketName': 'Socket-4', 'type': 'agent', 'materiaName': 'Builda', 'multiTurn': False}]})
            send_response({'type': 'materia_start', 'materiaName': 'Builda', 'socketName': 'Socket-4'})
            send_response({'type': 'materia_end', 'materiaName': 'Builda', 'socketName': 'Socket-4'})
            send_response({'type': 'agent_end', 'socketName': 'Socket-4', 'messages': []})
            send_response({'type': 'cast_end', 'castId': 'telemetry-ignore-test'})
            # Post a completed webhook so the run reaches a terminal state.
            time.sleep(0.05)
            post_event({'eventId': 'fp-completed', 'eventType': 'runtime.completed', 'occurredAt': '2026-06-23T00:00:01Z', 'severity': 'info', 'message': 'done', 'payload': {'outcome': 'no_changes_needed', 'summary': 'telemetry ignored, lifecycle routed'}})
            # Stay alive until the controller shuts us down.
            continue
        if msg.get('type') == 'abort':
            return

main()
""";

        /// <summary>
        /// Emits cast_start with a single agent socket, then agent_end, then cast_end.
        /// No webhook events are posted — cast_end is the terminal signal that triggers
        /// process shutdown, and the process exit handler synthesizes a completion event
        /// from exit code 0.
        /// </summary>
        public const string CastEndTerminal =
            Header
            + """
def main():
    while True:
        line = sys.stdin.readline()
        if not line:
            return
        try:
            msg = json.loads(line)
        except Exception:
            continue
        if msg.get('type') == 'prompt':
            send_response({'type': 'response', 'command': 'prompt', 'success': True})
            # cast_start with agent-controller preset.
            send_response({
                'type': 'cast_start',
                'castId': 'cast-end-terminal',
                'eventing': {'preset': 'agent-controller'},
                'sockets': [
                    {'socketName': 'Socket-4', 'type': 'agent', 'materiaName': 'Builda', 'multiTurn': False}
                ]
            })
            # agent_end — per-socket, non-terminal. Runtime stays alive.
            send_response({'type': 'agent_end', 'socketName': 'Socket-4', 'messages': []})
            # cast_end — whole-cast terminal signal. Runtime should shut down.
            send_response({'type': 'cast_end', 'castId': 'cast-end-terminal'})
            # Exit cleanly — the process exit handler synthesizes a terminal event from exit code 0.
            time.sleep(0.1)
            return
        if msg.get('type') == 'abort':
            return

main()
""";

        /// <summary>
        /// Multi-socket cast: emits cast_start with 3 agent sockets, then agent_end
        /// for each socket (non-terminal), then cast_end (terminal). No webhook events
        /// are posted — cast_end triggers process shutdown, and the process exit handler
        /// synthesizes a completion event from exit code 0.
        ///
        /// This tests the agent_end-stays-non-terminal / cast_end-is-terminal pairing:
        /// the runtime must NOT shut down on any of the agent_end events, but MUST
        /// shut down on cast_end.
        /// </summary>
        public const string MultiSocketAgentEndThenCastEnd =
            Header
            + """
def main():
    while True:
        line = sys.stdin.readline()
        if not line:
            return
        try:
            msg = json.loads(line)
        except Exception:
            continue
        if msg.get('type') == 'prompt':
            send_response({'type': 'response', 'command': 'prompt', 'success': True})
            # cast_start with 3 agent sockets (Plani → Builda → Evala).
            send_response({
                'type': 'cast_start',
                'castId': 'multi-socket-cast-end',
                'eventing': {'preset': 'agent-controller'},
                'sockets': [
                    {'socketName': 'Socket-3', 'type': 'agent', 'materiaName': 'Interactive-Plani', 'multiTurn': False},
                    {'socketName': 'Socket-4', 'type': 'agent', 'materiaName': 'Builda', 'multiTurn': False},
                    {'socketName': 'Socket-5', 'type': 'agent', 'materiaName': 'Auto-Evala', 'multiTurn': False}
                ]
            })
            # agent_end for Socket-3 (Interactive-Plani) — per-socket, non-terminal.
            send_response({'type': 'agent_end', 'socketName': 'Socket-3', 'messages': []})
            # agent_end for Socket-4 (Builda) — per-socket, non-terminal.
            send_response({'type': 'agent_end', 'socketName': 'Socket-4', 'messages': []})
            # agent_end for Socket-5 (Auto-Evala) — per-socket, non-terminal.
            send_response({'type': 'agent_end', 'socketName': 'Socket-5', 'messages': []})
            # cast_end — whole-cast terminal signal. Runtime should shut down.
            send_response({'type': 'cast_end', 'castId': 'multi-socket-cast-end'})
            # Exit cleanly — the process exit handler synthesizes a terminal event from exit code 0.
            time.sleep(0.1)
            return
        if msg.get('type') == 'abort':
            return

main()
""";
    }

    private sealed class TestHarness : IAsyncDisposable
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _listenerCts;
        private readonly Task _listenerTask;

        public TestHarness(
            string fakePiPath,
            string baseUrl,
            HttpListener listener,
            CancellationTokenSource listenerCts,
            Task listenerTask,
            string tempRoot
        )
        {
            FakePiPath = fakePiPath;
            BaseUrl = baseUrl;
            _listener = listener;
            _listenerCts = listenerCts;
            _listenerTask = listenerTask;
            TempRoot = tempRoot;
        }

        public string FakePiPath { get; }
        public string BaseUrl { get; }
        public string TempRoot { get; }
        public PiMateriaRuntime? Runtime { get; set; }

        public async ValueTask DisposeAsync()
        {
            Runtime?.Dispose();

            _listenerCts.Cancel();
            try
            {
                _listener.Stop();
            }
            catch
            { /* best-effort */
            }

            try
            {
                await _listenerTask.WaitAsync(TimeSpan.FromSeconds(3));
            }
            catch
            {
                // best-effort
            }

            _listenerCts.Dispose();
        }
    }

    // ── Minimal in-memory stores + stubs (same shape as the mock-runtime tests) ──

    private sealed class InMemoryAgentRunStore : IAgentRunStore
    {
        private readonly object _lock = new();
        private readonly Dictionary<string, AgentRunHandle> _runs = new();

        public Task<AgentRunHandle> CreateAsync(CreateRunRequest request, CancellationToken ct)
        {
            var run = new AgentRunHandle
            {
                RunId = $"run_{Guid.NewGuid():N}",
                WorkItemId = request.WorkItemId,
                Status = request.InitialStatus,
                StartedAt =
                    request.InitialStatus > RunLifecycleState.Claimed
                        ? DateTimeOffset.UtcNow
                        : null,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            lock (_lock)
            {
                _runs[run.RunId] = run;
            }
            return Task.FromResult(run);
        }

        public Task<AgentRunHandle?> GetByIdAsync(string runId, CancellationToken ct)
        {
            lock (_lock)
            {
                _runs.TryGetValue(runId, out var run);
                return Task.FromResult(run);
            }
        }

        public Task UpdateStatusAsync(string runId, RunLifecycleState status, CancellationToken ct)
        {
            lock (_lock)
            {
                if (_runs.TryGetValue(runId, out var run))
                {
                    _runs[runId] = run with
                    {
                        Status = status,
                        UpdatedAt = DateTimeOffset.UtcNow,
                        FinishedAt =
                            run.FinishedAt ?? (IsTerminal(status) ? DateTimeOffset.UtcNow : null),
                    };
                }
            }
            return Task.CompletedTask;
        }

        public Task UpdateRuntimeFieldsAsync(
            string runId,
            RuntimeFieldUpdate update,
            CancellationToken ct
        )
        {
            lock (_lock)
            {
                if (_runs.TryGetValue(runId, out var run))
                {
                    _runs[runId] = run with
                    {
                        RuntimeRunId = update.RuntimeRunId ?? run.RuntimeRunId,
                        BranchName = update.BranchName ?? run.BranchName,
                        PullRequestUrl = update.PullRequestUrl ?? run.PullRequestUrl,
                        ResultSummary = update.ResultSummary ?? run.ResultSummary,
                        FinishedAt = update.FinishedAt ?? run.FinishedAt,
                        LastHeartbeatAt = update.LastHeartbeatAt ?? run.LastHeartbeatAt,
                        Error = update.Error ?? run.Error,
                        UpdatedAt = DateTimeOffset.UtcNow,
                    };
                }
            }
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AgentRunHandle>> ListAsync(
            ListRunsQuery query,
            CancellationToken ct
        )
        {
            lock (_lock)
            {
                return Task.FromResult<IReadOnlyList<AgentRunHandle>>(_runs.Values.ToList());
            }
        }

        public Task<IReadOnlyList<AgentRunHandle>> FindStaleAsync(
            TimeSpan staleTimeout,
            CancellationToken ct
        ) => Task.FromResult<IReadOnlyList<AgentRunHandle>>([]);

        public Task<int> CountActiveAsync(CancellationToken ct)
        {
            lock (_lock)
            {
                return Task.FromResult(_runs.Values.Count(r => !IsTerminal(r.Status)));
            }
        }

        public Task<AgentRunHandle?> FindLatestRunByWorkItemAsync(string workItemId, CancellationToken ct)
        {
            lock (_lock)
            {
                var latest = _runs.Values
                    .Where(r => r.WorkItemId == workItemId)
                    .OrderByDescending(r => r.CreatedAt)
                    .FirstOrDefault();
                return Task.FromResult<AgentRunHandle?>(latest);
            }
        }

        private static bool IsTerminal(RunLifecycleState s) =>
            s
                is RunLifecycleState.Completed
                    or RunLifecycleState.Failed
                    or RunLifecycleState.Cancelled
                    or RunLifecycleState.CleanedUp
                    or RunLifecycleState.PrOpened
                    or RunLifecycleState.BranchPushed
                    or RunLifecycleState.NeedsHuman;
    }

    private sealed class InMemoryLifecycleEventStore : ILifecycleEventStore
    {
        private readonly object _lock = new();
        private readonly List<LifecycleEvent> _events = new();

        /// <summary>Test-only synchronous snapshot of recorded events for a run.</summary>
        public List<LifecycleEvent> Snapshot(string runId)
        {
            lock (_lock)
            {
                return _events.Where(e => e.RunId == runId).OrderBy(e => e.CreatedAt).ToList();
            }
        }

        public Task AppendAsync(LifecycleEvent evt, CancellationToken ct)
        {
            lock (_lock)
            {
                _events.Add(
                    evt with
                    {
                        Id = string.IsNullOrWhiteSpace(evt.Id) ? $"evt_{Guid.NewGuid():N}" : evt.Id,
                    }
                );
            }
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<LifecycleEvent>> ListByRunIdAsync(
            string runId,
            CancellationToken ct
        )
        {
            lock (_lock)
            {
                return Task.FromResult<IReadOnlyList<LifecycleEvent>>(
                    _events.Where(e => e.RunId == runId).OrderBy(e => e.CreatedAt).ToList()
                );
            }
        }

        public Task<bool> ExistsByEventIdAsync(string runId, string eventId, CancellationToken ct)
        {
            lock (_lock)
            {
                return Task.FromResult(_events.Any(e => e.RunId == runId && e.EventId == eventId));
            }
        }
    }

    private sealed class InMemoryWorkItemStore : IWorkItemStore
    {
        private readonly Dictionary<string, WorkCandidate> _items = new();

        public Task<WorkCandidate> CreateAsync(CreateWorkItemRequest request, CancellationToken ct)
        {
            var item = new WorkCandidate
            {
                Id = $"wi_{Guid.NewGuid():N}",
                ExternalId = "local-fake",
                RepoKey = request.RepoKey,
                Title = request.Title,
                Source = request.Source,
            };
            _items[item.Id] = item;
            return Task.FromResult(item);
        }

        public Task<IReadOnlyList<WorkCandidate>> ListAsync(
            ListWorkItemsQuery query,
            CancellationToken ct
        ) => Task.FromResult<IReadOnlyList<WorkCandidate>>(_items.Values.ToList());

        public Task<WorkCandidate?> GetByIdAsync(string id, CancellationToken ct)
        {
            _items.TryGetValue(id, out var item);
            return Task.FromResult(item);
        }

        public Task<IReadOnlyList<WorkCandidate>> FindEligibleAsync(
            WorkQuery query,
            CancellationToken ct
        ) => Task.FromResult<IReadOnlyList<WorkCandidate>>(_items.Values.ToList());

        public Task<ClaimResult> TryClaimAsync(
            string workItemId,
            ClaimRequest claim,
            CancellationToken ct
        ) => Task.FromResult(new ClaimResult { Success = true });

        public Task UpdateStatusAsync(string workItemId, string status, CancellationToken ct) =>
            Task.CompletedTask;

        public Task<WorkCandidate> UpsertAsync(WorkCandidate candidate, CancellationToken ct)
        {
            var id = candidate.Id.Length > 0 ? candidate.Id : $"wi_{Guid.NewGuid():N}";
            var item = candidate with { Id = id };
            _items[id] = item;
            return Task.FromResult(item);
        }
    }

    private sealed class StubWorkSource : IWorkSource
    {
        public Task<IReadOnlyList<WorkCandidate>> FindEligibleAsync(
            WorkQuery query,
            CancellationToken ct
        ) => Task.FromResult<IReadOnlyList<WorkCandidate>>([]);

        public Task<ClaimResult> TryClaimAsync(
            WorkCandidate candidate,
            ClaimRequest claim,
            CancellationToken ct
        ) => Task.FromResult(new ClaimResult { Success = true });

        public Task UpdateStatusAsync(
            ExternalWorkRef workRef,
            ExternalWorkStatus status,
            CancellationToken ct
        ) => Task.CompletedTask;

        public Task AddCommentAsync(
            ExternalWorkRef workRef,
            string comment,
            CancellationToken ct
        ) => Task.CompletedTask;

        public Task<IReadOnlyList<WorkItemComment>> GetCommentsAsync(
            ExternalWorkRef workRef,
            int maxComments,
            CancellationToken ct
        ) => Task.FromResult<IReadOnlyList<WorkItemComment>>(Array.Empty<WorkItemComment>());

        public Task ReleaseClaimAsync(
            ReleaseClaimRequest request,
            CancellationToken ct
        ) => Task.CompletedTask;
    }

    private sealed class StaticOptionsMonitor<TOptions> : IOptionsMonitor<TOptions>
        where TOptions : class
    {
        public StaticOptionsMonitor(TOptions currentValue) => CurrentValue = currentValue;

        public TOptions CurrentValue { get; }

        public TOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<TOptions, string?> listener) => null;
    }
}

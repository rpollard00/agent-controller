using System.Net;
using System.Net.Sockets;
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
            RunListQuery query,
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
            WorkItemListQuery query,
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

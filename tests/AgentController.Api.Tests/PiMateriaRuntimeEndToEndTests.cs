using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentController.Api;
using AgentController.Api.Models;
using AgentController.Application;
using AgentController.Domain;
using AgentController.Infrastructure;
using AgentController.Infrastructure.Data;
using AgentController.Infrastructure.Options;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentController.Api.Tests;

/// <summary>
/// End-to-end tests that wire the full chain with a real Kestrel host:
/// real PollingWorker poll cycle → real PiMateriaRuntime → generated fake-pi child process →
/// real POST /runs/{runId}/events endpoint → terminal state.
///
/// Unlike LocalEndToEndSmokeTests (mock runtime, in-process events) and
/// PiMateriaRuntimeTests (real runtime + fake pi, but no poll loop), these tests
/// prove the three layers work together: the poll loop hands off to the real
/// runtime, the fake pi's webhooks flow through the real HTTP endpoint, and
/// the run completes.
///
/// The fake pi is a separate OS process that POSTs over HTTP, so the controller's
/// event endpoint must be reachable on a real TCP port. WebApplicationFactory
/// TestServer is in-process-only and will not be reachable from the child process.
/// The test allocates an ephemeral port and runs the real Kestrel host.
///
/// Approach B: the hosted PollingWorker is disabled (workerEnabled=false);
/// the test drives <see cref="PollingWorker.RunPollCycleForTestingAsync"/> directly
/// against the same host's services, with the HTTP endpoint live for callbacks.
/// </summary>
#pragma warning disable CA1001 // IAsyncLifetime.DisposeAsync handles cleanup
public class PiMateriaRuntimeEndToEndTests : IAsyncLifetime
#pragma warning restore CA1001
{
    private string _tempRoot = null!;
    private string _tempDbPath = null!;
    private string _tempRepoPath = null!;
    private string _tempRunRoot = null!;
    private string _fakePiPath = null!;
    private int _port;
    private WebApplication? _host;
    private HttpClient? _httpClient;

    public async Task InitializeAsync()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"agent-e2e-pimateria-{Guid.NewGuid():N}");
        _tempDbPath = Path.Combine(_tempRoot, "test.db");
        _tempRepoPath = Path.Combine(_tempRoot, "test-repo");
        _tempRunRoot = Path.Combine(_tempRoot, "runs");
        _fakePiPath = Path.Combine(_tempRoot, "fake-pi.py");

        Directory.CreateDirectory(_tempRoot);
        Directory.CreateDirectory(_tempRepoPath);

        // Initialize a minimal git repository.
        await RunGitAsync(_tempRepoPath, ["init", "--initial-branch=main"], TimeSpan.FromSeconds(10));
        await RunGitAsync(_tempRepoPath, ["config", "user.email", "test@example.com"], TimeSpan.FromSeconds(5));
        await RunGitAsync(_tempRepoPath, ["config", "user.name", "Test User"], TimeSpan.FromSeconds(5));
        await File.WriteAllTextAsync(Path.Combine(_tempRepoPath, "README.md"), "# Test Repo");
        await RunGitAsync(_tempRepoPath, ["add", "README.md"], TimeSpan.FromSeconds(5));
        await RunGitAsync(_tempRepoPath, ["commit", "-m", "Initial commit"], TimeSpan.FromSeconds(5));

        // Write the fake-pi HappyPr script.
        WriteFakePiScript(_fakePiPath);
    }

    public async Task DisposeAsync()
    {
        // Dispose the runtime (kills any remaining fake-pi processes).
        if (_host?.Services.GetService<IAgentRuntime>() is IDisposable disposableRuntime)
        {
            disposableRuntime.Dispose();
        }

        // Stop the host.
        if (_host is not null)
        {
            try
            {
                await _host.StopAsync(TimeSpan.FromSeconds(5));
            }
            catch
            {
                // Best-effort.
            }

            await _host.DisposeAsync();
            _host = null;
        }

        _httpClient?.Dispose();
        _httpClient = null;

        // Clean up temp directory.
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    [Fact(Timeout = 30000)]
    public async Task PollCycle_RealPiMateriaRuntime_FakePiWebhooks_RunReachesTerminal()
    {
        // ── 1. Allocate ephemeral port ──────────────────────────────
        _port = AllocateEphemeralPort();
        var baseUrl = $"http://127.0.0.1:{_port}";

        // ── 2. Build configuration ──────────────────────────────────
        // workerEnabled=false — we drive the poll cycle manually.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["agentController:workerId"] = "test-e2e-pimateria-worker",
                ["agentController:pollIntervalSeconds"] = "2",
                ["agentController:maxConcurrentRuns"] = "1",
                ["agentController:staleTimeoutSeconds"] = "300",
                ["agentController:runRoot"] = _tempRunRoot,
                ["agentController:retainSuccessfulRuns"] = "true",
                ["agentController:retainFailedRuns"] = "true",
                ["agentController:workerEnabled"] = "false",
                ["persistence:provider"] = "Sqlite",
                ["persistence:connectionString"] = $"Data Source={_tempDbPath}",
                ["workSource:provider"] = "LocalFile",
                ["sourceControl:provider"] = "LocalGit",
                ["environmentProvider:provider"] = "LocalWorkspace",
                ["runtime:provider"] = "PiMateria",
                ["runtime:piExecutablePath"] = _fakePiPath,
                ["runtime:controllerBaseUrl"] = baseUrl,
                ["runtime:defaultMateriaLoadout"] = "Wedge",
                ["runtime:promptAcceptanceTimeoutSeconds"] = "5",
                ["runtime:heartbeatIntervalSeconds"] = "2",
                ["runtime:cancelGracePeriodSeconds"] = "1",
                ["localWork:definitions:0:repoKey"] = "test-repo",
                ["localWork:definitions:0:title"] = "E2E PiMateria: Implement demo feature",
                ["localWork:definitions:0:body"] = "End-to-end test verifying real poll loop + real PiMateriaRuntime + fake pi webhooks.",
                ["localWork:definitions:0:tags:0"] = "agent-ready",
                ["localWork:definitions:0:priority"] = "1",
                ["localWork:definitions:0:status"] = "New",
                ["repositories:test-repo:cloneUrl"] = _tempRepoPath,
                ["repositories:test-repo:defaultBranch"] = "main",
                ["repositories:test-repo:environmentProfile"] = "local-default",
                ["repositories:test-repo:runtimeProfile"] = "pi-materia-default",
            })
            .Build();

        // ── 3. Build and start the real Kestrel host ────────────────
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = _tempRoot,
            Args = [],
        });

        // Layer our in-memory config on top so it overrides any defaults.
        builder.Configuration.AddInMemoryCollection(config.AsEnumerable());

        builder.Services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Information));

        // Configure JSON options to match Program.cs — required for enum-as-string
        // deserialization (e.g. severity: "info") and proper payload handling.
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.Converters.Add(
                new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        });

        builder.Services.AddAgentControllerOptions(builder.Configuration);
        builder.Services.AddAgentControllerDbContext(builder.Configuration);
        builder.Services.AddAgentControllerRepositories();
        builder.Services.AddAgentControllerLifecycleService();
        builder.Services.AddAgentControllerNoOpProviders();

        // Wire real providers (last-registered wins over no-ops)
        builder.Services.AddAgentControllerLocalFileWorkSource();
        builder.Services.AddAgentControllerLocalGitSourceControl();
        builder.Services.AddAgentControllerLocalWorkspaceEnvironment();
        builder.Services.AddAgentControllerPiMateriaRuntime();

        builder.WebHost.UseUrls(baseUrl);

        _host = builder.Build();

        // Map the minimal API endpoints needed for the e2e test.
        _host.MapGet("/health", () => Results.Ok(new { Status = "Healthy" }));

        _host.MapPost(
            "/runs/{runId}/events",
            async (
                string runId,
                RuntimeEventRequest request,
                IRunLifecycleService lifecycle,
                IAgentRunStore runStore,
                CancellationToken ct) =>
            {
                if (string.IsNullOrWhiteSpace(request.EventType))
                    return Results.BadRequest(new { error = "Missing eventType" });
                if (string.IsNullOrWhiteSpace(request.EventId))
                    return Results.BadRequest(new { error = "Missing eventId" });

                var evt = new RuntimeEvent
                {
                    EventId = request.EventId,
                    RunId = runId,
                    EventType = request.EventType,
                    RuntimeRunId = request.RuntimeRunId,
                    Sequence = request.Sequence,
                    OccurredAt = request.OccurredAt ?? DateTimeOffset.UtcNow,
                    Severity = request.Severity ?? EventSeverity.Info,
                    Message = request.Message,
                    Payload = request.Payload,
                };

                try
                {
                    await lifecycle.IngestRuntimeEventAsync(evt, ct);
                }
                catch (InvalidOperationException ex)
                {
                    var isDuplicate = ex.Message.Contains("already been processed", StringComparison.OrdinalIgnoreCase);
                    return isDuplicate
                        ? Results.Conflict(new { error = ex.Message, runId, eventId = request.EventId })
                        : Results.UnprocessableEntity(new { error = ex.Message, runId, eventId = request.EventId });
                }

                var updatedRun = await runStore.GetByIdAsync(runId, ct);
                return updatedRun is null
                    ? Results.NotFound(new { error = $"Run '{runId}' not found." })
                    : Results.Ok(new
                    {
                        runId = updatedRun.RunId,
                        status = updatedRun.Status.ToString(),
                        eventId = request.EventId,
                    });
            });

        _host.MapGet(
            "/runs",
            async (
                string? status,
                string? workItemId,
                int? maxResults,
                int? offset,
                IAgentRunStore runStore,
                IWorkItemStore workItemStore,
                CancellationToken ct) =>
            {
                RunLifecycleState? statusFilter = null;
                if (!string.IsNullOrWhiteSpace(status)
                    && Enum.TryParse<RunLifecycleState>(status, ignoreCase: true, out var parsed))
                {
                    statusFilter = parsed;
                }

                var query = new RunListQuery
                {
                    Status = statusFilter,
                    WorkItemId = workItemId,
                    MaxResults = maxResults ?? 100,
                    Offset = offset ?? 0,
                };

                var runs = await runStore.ListAsync(query, ct);

                var items = new List<RunListResponse>(runs.Count);
                foreach (var run in runs)
                {
                    string? workItemTitle = null;
                    string? repoKey = null;
                    if (!string.IsNullOrWhiteSpace(run.WorkItemId))
                    {
                        var wi = await workItemStore.GetByIdAsync(run.WorkItemId, ct);
                        if (wi is not null)
                        {
                            workItemTitle = wi.Title;
                            repoKey = wi.RepoKey;
                        }
                    }

                    items.Add(new RunListResponse
                    {
                        RunId = run.RunId,
                        WorkItemId = run.WorkItemId,
                        WorkItemTitle = workItemTitle,
                        RepoKey = repoKey,
                        Status = run.Status.ToString(),
                        StartedAt = run.StartedAt,
                        FinishedAt = run.FinishedAt,
                        LastHeartbeatAt = run.LastHeartbeatAt,
                        CreatedAt = run.CreatedAt,
                    });
                }

                return Results.Ok(new RunListEnvelope
                {
                    Runs = items,
                    TotalCount = items.Count,
                });
            });

        _host.MapGet(
            "/runs/{runId}",
            async (
                string runId,
                IAgentRunStore runStore,
                IWorkItemStore workItemStore,
                ILifecycleEventStore lifecycleStore,
                IEnvironmentStore environmentStore,
                CancellationToken ct) =>
            {
                var run = await runStore.GetByIdAsync(runId, ct);
                if (run is null)
                    return Results.NotFound(new { error = $"Run '{runId}' not found." });

                WorkCandidate? workItem = null;
                if (!string.IsNullOrWhiteSpace(run.WorkItemId))
                {
                    workItem = await workItemStore.GetByIdAsync(run.WorkItemId, ct);
                }

                EnvironmentHandle? environment = null;
                if (!string.IsNullOrWhiteSpace(run.EnvironmentId))
                {
                    environment = await environmentStore.GetByIdAsync(run.EnvironmentId, ct);
                }

                var lifecycleEvents = await lifecycleStore.ListByRunIdAsync(runId, ct);

                var detail = new RunDetailResponse
                {
                    RunId = run.RunId,
                    WorkItemId = run.WorkItemId,
                    WorkItem = workItem,
                    EnvironmentId = run.EnvironmentId,
                    RuntimeType = run.RuntimeType,
                    RuntimeRunId = run.RuntimeRunId,
                    Status = run.Status.ToString(),
                    BranchName = run.BranchName,
                    PullRequestUrl = run.PullRequestUrl,
                    ResultSummary = run.ResultSummary,
                    StartedAt = run.StartedAt,
                    FinishedAt = run.FinishedAt,
                    LastHeartbeatAt = run.LastHeartbeatAt,
                    Error = run.Error,
                    Environment = environment,
                    LifecycleEvents = lifecycleEvents,
                    CreatedAt = run.CreatedAt,
                    UpdatedAt = run.UpdatedAt,
                };

                return Results.Ok(detail);
            });

        // Ensure DB is created.
        await using (var scope = _host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentControllerDbContext>();
            await db.Database.EnsureCreatedAsync();
        }

        await _host.StartAsync();

        // ── 4. Create HttpClient for polling ────────────────────────
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(baseUrl),
            Timeout = TimeSpan.FromSeconds(10),
        };

        // ── 5. Resolve the PollingWorker and run a poll cycle ───────
        // The worker is registered as a hosted service but workerEnabled=false,
        // so it won't poll automatically. We resolve it and drive it manually.
        var scopeFactory = _host.Services.GetRequiredService<IServiceScopeFactory>();
        var optionsMonitor = _host.Services.GetRequiredService<IOptionsMonitor<AgentControllerOptions>>();
        var logger = _host.Services.GetRequiredService<ILogger<PollingWorker>>();

        var worker = new PollingWorker(scopeFactory, optionsMonitor, logger);

        using var pollCts = new CancellationTokenSource(TimeSpan.FromSeconds(25));

        // Run a single poll cycle — discovers the work item, provisions environment,
        // clones repo, injects context, hands off to PiMateriaRuntime (spawns fake-pi).
        await worker.RunPollCycleForTestingAsync(pollCts.Token);

        // ── 6. Wait for the fake pi to POST events and the run to reach terminal ──
        // The poll cycle returns after advancing to AwaitingResult.
        // The fake-pi process is running in the background and will POST events
        // to the real HTTP endpoint. We poll GET /runs/{runId} until terminal.
        var terminalStates = new[]
        {
            "PrOpened",
            "Completed",
            "Failed",
            "Cancelled",
            "BranchPushed",
            "NeedsHuman",
            "CleanedUp",
        };

        string? runId = null;
        string? finalStatus = null;
        string? finalJson = null;

        // First, discover the run ID.
        {
            var response = await _httpClient.GetAsync("/runs", pollCts.Token);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(pollCts.Token);
            using var doc = JsonDocument.Parse(json);
            var runs = doc.RootElement.GetProperty("runs");
            if (runs.GetArrayLength() > 0)
            {
                runId = runs[0].GetProperty("runId").GetString();
            }
        }

        Assert.NotNull(runId);

        // Now poll until terminal.
        while (!pollCts.Token.IsCancellationRequested)
        {
            var response = await _httpClient.GetAsync($"/runs/{runId}", pollCts.Token);
            response.EnsureSuccessStatusCode();

            finalJson = await response.Content.ReadAsStringAsync(pollCts.Token);
            using var doc = JsonDocument.Parse(finalJson);
            finalStatus = doc.RootElement.GetProperty("status").GetString();

            if (terminalStates.Contains(finalStatus))
            {
                break;
            }

            await Task.Delay(500, pollCts.Token);
        }

        // If we timed out, dump diagnostics.
        if (string.IsNullOrEmpty(finalStatus) || !terminalStates.Contains(finalStatus))
        {
            // Try one more read to get the final state for diagnostics.
            try
            {
                var diagResponse = await _httpClient!.GetAsync($"/runs/{runId}");
                if (diagResponse.IsSuccessStatusCode)
                {
                    finalJson = await diagResponse.Content.ReadAsStringAsync();
                    using var diagDoc = JsonDocument.Parse(finalJson);
                    finalStatus = diagDoc.RootElement.GetProperty("status").GetString();
                }
            }
            catch
            {
                // Best-effort diagnostics.
            }
        }

        Assert.NotNull(finalStatus);
        Assert.NotNull(finalJson);

        // ── 7. Assert the run reached a happy-path terminal state ───
        Assert.True(
            finalStatus == "PrOpened" || finalStatus == "Completed",
            $"Expected run to reach PrOpened or Completed, but was: {finalStatus}. " +
            $"Full response: {finalJson}"
        );

        // Parse the run detail to assert PR URL and lifecycle events.
        using var finalDoc = JsonDocument.Parse(finalJson!);

        // Assert PullRequestUrl is present.
        var prUrl = finalDoc.RootElement.TryGetProperty("pullRequestUrl", out var prEl)
            ? prEl.GetString()
            : null;
        Assert.NotNull(prUrl);

        // Assert lifecycle events include the full controller sequence.
        var lifecycleEvents = finalDoc.RootElement.GetProperty("lifecycleEvents");
        var eventTypes = lifecycleEvents.EnumerateArray()
            .Select(e => e.GetProperty("eventType").GetString())
            .ToList();

        // Controller-owned events from the poll cycle.
        Assert.Contains("controller.claimed", eventTypes);
        Assert.Contains("controller.environment_provisioning", eventTypes);
        Assert.Contains("controller.environment_ready", eventTypes);
        Assert.Contains("controller.repository_cloning", eventTypes);
        Assert.Contains("controller.repository_ready", eventTypes);
        Assert.Contains("controller.context_injected", eventTypes);
        Assert.Contains("controller.agent_starting", eventTypes);
        Assert.Contains("controller.agent_running", eventTypes);
        Assert.Contains("controller.awaiting_result", eventTypes);

        // Runtime events from the fake pi's webhooks through the real HTTP endpoint.
        Assert.Contains("runtime.accepted", eventTypes);
        Assert.Contains("runtime.status", eventTypes);
        Assert.Contains("runtime.completed", eventTypes);

        // Assert runtime.completed has the expected outcome.
        var completedEvent = lifecycleEvents.EnumerateArray()
            .FirstOrDefault(e => e.GetProperty("eventType").GetString() == "runtime.completed");
        Assert.True(completedEvent.ValueKind != JsonValueKind.Undefined,
            "runtime.completed event not found in lifecycle events");

        var payload = completedEvent.GetProperty("payload");
        var outcome = payload.GetProperty("outcome").GetString();
        Assert.Equal("pull_request_opened", outcome);
    }

    // ── Helpers ─────────────────────────────────────────────────────

    /// <summary>
    /// Allocate an ephemeral TCP port using a TcpListener bound to port 0.
    /// The OS assigns an available port which we extract and release.
    /// </summary>
    private static int AllocateEphemeralPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>
    /// Write the HappyPr fake-pi Python script. The script:
    /// 1. Reads JSONL from stdin (RPC protocol).
    /// 2. On 'prompt' command, sends response line then POSTs
    ///    runtime.accepted → runtime.status → runtime.completed
    ///    (with pull_request_opened outcome) to CONTROLLER_EVENT_URL.
    /// 3. Stays alive after posting completed so the runtime exercises
    ///    its "webhook drove terminal → abort → kill" graceful shutdown.
    /// 4. On 'abort' command, exits cleanly.
    ///
    /// Severity is lowercase 'info' — implicit regression guard for the
    /// string-enum deserialization bug.
    /// </summary>
    private static void WriteFakePiScript(string path)
    {
        const string script =
            "#!/usr/bin/env python3\n"
            + "\"\"\"Fake pi RPC process for PiMateriaRuntimeEndToEndTests.\"\"\"\n"
            + "import json, os, sys, time, urllib.request\n\n"
            + "EVENT_URL = os.environ.get('CONTROLLER_EVENT_URL', '')\n"
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
            + "        diag('no EVENT_URL configured')\n"
            + "        return\n"
            + "    data = json.dumps(payload).encode('utf-8')\n"
            + "    req = urllib.request.Request(EVENT_URL, data=data,\n"
            + "        headers={'Content-Type': 'application/json'})\n"
            + "    try:\n"
            + "        resp = urllib.request.urlopen(req, timeout=5)\n"
            + "        diag(f'post {payload.get(\"eventType\", \"?\")} -> {resp.status}')\n"
            + "    except Exception as e:\n"
            + "        diag(f'post failed: {e}')\n\n"
            + "def main():\n"
            + "    while True:\n"
            + "        line = sys.stdin.readline()\n"
            + "        if not line:\n"
            + "            return\n"
            + "        try:\n"
            + "            msg = json.loads(line)\n"
            + "        except Exception:\n"
            + "            continue\n"
            + "        if msg.get('type') == 'prompt':\n"
            + "            diag('received prompt')\n"
            + "            send_response({'type': 'response', 'command': 'prompt', 'success': True})\n"
            + "            # Post accepted (severity lowercase 'info' — regression guard).\n"
            + "            post_event({\n"
            + "                'eventId': 'fp-accepted',\n"
            + "                'eventType': 'runtime.accepted',\n"
            + "                'occurredAt': '2026-06-23T00:00:00Z',\n"
            + "                'severity': 'info',\n"
            + "                'message': 'accepted'\n"
            + "            })\n"
            + "            time.sleep(0.05)\n"
            + "            post_event({\n"
            + "                'eventId': 'fp-status',\n"
            + "                'eventType': 'runtime.status',\n"
            + "                'message': 'working'\n"
            + "            })\n"
            + "            time.sleep(0.05)\n"
            + "            post_event({\n"
            + "                'eventId': 'fp-completed',\n"
            + "                'eventType': 'runtime.completed',\n"
            + "                'occurredAt': '2026-06-23T00:00:01Z',\n"
            + "                'severity': 'info',\n"
            + "                'message': 'done',\n"
            + "                'payload': {\n"
            + "                    'outcome': 'pull_request_opened',\n"
            + "                    'summary': 'fake PR',\n"
            + "                    'branchName': 'agent/test',\n"
            + "                    'pullRequestUrl': 'http://example.test/pr/1'\n"
            + "                }\n"
            + "            })\n"
            + "            # Stay alive until the controller shuts us down.\n"
            + "            continue\n"
            + "        if msg.get('type') == 'abort':\n"
            + "            diag('abort received, exiting')\n"
            + "            return\n\n"
            + "main()\n";

        File.WriteAllText(path, script);

        // Make it directly executable (best-effort on non-Linux).
        try
        {
            var info = new ProcessStartInfo("chmod", ["+x", path])
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            Process.Start(info)?.WaitForExit(2000);
        }
        catch
        {
            // chmod is best-effort; the Python shebang + python3 fallback still works.
        }
    }

    /// <summary>
    /// Run a git command in the specified working directory.
    /// </summary>
    private static async Task RunGitAsync(
        string workingDirectory,
        string[] arguments,
        TimeSpan timeout)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

        foreach (var arg in arguments)
        {
            process.StartInfo.ArgumentList.Add(arg);
        }

        using var cts = new CancellationTokenSource(timeout);

        process.Start();

        var stdOutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
        var stdErrTask = process.StandardError.ReadToEndAsync(cts.Token);

        await process.WaitForExitAsync(cts.Token);

        var stdErr = await stdErrTask;

        if (process.ExitCode != 0)
        {
            var stdOut = await stdOutTask;
            throw new InvalidOperationException(
                $"git {string.Join(" ", arguments)} failed (exit {process.ExitCode}):\n{stdOut}\n{stdErr}");
        }
    }
}

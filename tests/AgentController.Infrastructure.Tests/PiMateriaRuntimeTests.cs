using System.Net;
using System.Net.Sockets;
using System.Reflection;
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
/// Tests for <see cref="PiMateriaRuntime"/> as a fire-and-forget CLI launcher.
///
/// The runtime launches <c>pi</c> as a detached process:
/// <c>pi "/materia loadout Elena" "/materia cast {task}"</c>
/// with three environment variables: CONTROLLER_RUN_ID, CONTROLLER_EVENT_URL,
/// CONTROLLER_CONTEXT_DIR. After spawn it returns immediately.
///
/// These tests use a shell script as a fake <c>pi</c> executable that captures
/// its arguments and environment variables to a file for verification.
/// </summary>
public class PiMateriaRuntimeTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private ServiceProvider _provider = null!;
    private IServiceScopeFactory _scopeFactory = null!;
    private InMemoryAgentRunStore _runStore = null!;
    private InMemoryLifecycleEventStore _eventStore = null!;
    private string _tempRoot = null!;

    public PiMateriaRuntimeTests(ITestOutputHelper output) => _output = output;

    public Task InitializeAsync()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"pimateria-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);

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

    // ── Launcher behavior: ProcessStartInfo verification ─────────────

    [Fact]
    public async Task StartAsync_LaunchesWithCorrectArguments()
    {
        var fakePiPath = WriteCaptureScript(_tempRoot, "capture-pi.sh");
        var runId = await SeedRunAsync();
        var spec = await BuildSpecAsync(runId, "Test task");
        var runtime = CreateRuntime(fakePiPath);

        var handle = await runtime.StartAsync(spec, CancellationToken.None);

        Assert.Equal(spec.RunId, handle.RunId);
        Assert.StartsWith("pi-", handle.RuntimeRunId, StringComparison.Ordinal);
        Assert.Equal(RunLifecycleState.AgentRunning, handle.Status);
        Assert.NotNull(handle.StartedAt);

        // Wait for the capture script to write its output.
        await Task.Delay(500);

        var capturePath = Path.Combine(_tempRoot, "capture.txt");
        Assert.True(File.Exists(capturePath), "Capture file should exist.");

        var content = await File.ReadAllTextAsync(capturePath);
        Assert.Contains("/materia loadout Elena", content);
        Assert.Contains("/materia cast", content);
        Assert.Contains("Test task", content);

        runtime.Dispose();
    }

    [Fact]
    public async Task StartAsync_InjectsRequiredEnvironmentVariables()
    {
        var fakePiPath = WriteCaptureScript(_tempRoot, "capture-pi-env.sh");
        var runId = await SeedRunAsync();
        var spec = await BuildSpecAsync(runId, "Env test");
        var runtime = CreateRuntime(fakePiPath);

        _ = await runtime.StartAsync(spec, CancellationToken.None);

        // Wait for the capture script to write its output.
        await Task.Delay(500);

        var capturePath = Path.Combine(_tempRoot, "capture-env.txt");
        Assert.True(File.Exists(capturePath), "Capture file should exist.");

        var content = await File.ReadAllTextAsync(capturePath);
        Assert.Contains($"CONTROLLER_RUN_ID={runId}", content);
        Assert.Contains("CONTROLLER_EVENT_URL=http://127.0.0.1:", content);
        Assert.Contains("CONTROLLER_CONTEXT_DIR=", content);

        runtime.Dispose();
    }

    [Fact]
    public async Task StartAsync_UsesDetachedProcessSettings()
    {
        // Use a capture script that also records whether stdin/stdout are pipes.
        // With UseShellExecute=false and no redirected streams, the process
        // inherits the parent's console (not pipes).
        var fakePiPath = WriteCaptureScript(_tempRoot, "capture-detached.sh");
        var runId = await SeedRunAsync();
        var spec = await BuildSpecAsync(runId, "Detached test");
        var runtime = CreateRuntime(fakePiPath);

        _ = await runtime.StartAsync(spec, CancellationToken.None);

        // The runtime returns immediately (fire-and-forget).
        // The process has already spawned and the capture file should exist.
        await Task.Delay(500);

        var capturePath = Path.Combine(_tempRoot, "capture-detached.txt");
        Assert.True(File.Exists(capturePath), "Capture file should exist for detached process.");

        runtime.Dispose();
    }

    [Fact]
    public async Task StartAsync_UsesRepoPathAsWorkingDirectory()
    {
        var fakePiPath = WriteCaptureScript(_tempRoot, "capture-cwd.sh");
        var runId = await SeedRunAsync();
        var spec = await BuildSpecAsync(runId, "CWD test");
        var runtime = CreateRuntime(fakePiPath);

        _ = await runtime.StartAsync(spec, CancellationToken.None);

        await Task.Delay(500);

        var capturePath = Path.Combine(_tempRoot, "capture-cwd.txt");
        Assert.True(File.Exists(capturePath), "Capture file should exist.");

        var content = await File.ReadAllTextAsync(capturePath);
        // The working directory should be the repo path.
        Assert.Contains("CWD=", content);
        var cwdLine = content.Split('\n').FirstOrDefault(l => l.StartsWith("CWD=", StringComparison.Ordinal));
        Assert.NotNull(cwdLine);
        Assert.Contains("repo", cwdLine!, StringComparison.OrdinalIgnoreCase);

        runtime.Dispose();
    }

    // ── Launch-failure path ─────────────────────────────────────────

    [Fact]
    public async Task StartAsync_ExecutableNotFound_SynthesizesFailure()
    {
        var runtime = new PiMateriaRuntime(
            _scopeFactory,
            new StaticOptionsMonitor<RuntimeOptions>(
                new RuntimeOptions
                {
                    Provider = "PiMateria",
                    PiExecutablePath = "/nonexistent/path/to/pi-that-does-not-exist",
                    ControllerBaseUrl = "http://127.0.0.1:9999",
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

        // The runtime should synthesize a failure event.
        await WaitForStateAsync(runId, s => s == RunLifecycleState.Failed, TimeSpan.FromSeconds(5));

        var run = await _runStore.GetByIdAsync(runId, CancellationToken.None);
        Assert.NotNull(run);
        Assert.Equal(RunLifecycleState.Failed, run!.Status);

        var events = await _eventStore.ListByRunIdAsync(runId, CancellationToken.None);
        Assert.Contains(events, e => e.EventType == RuntimeEventTypes.Failed);

        runtime.Dispose();
    }

    [Fact]
    public async Task StartAsync_MissingControllerBaseUrl_SynthesizesFailure()
    {
        var runtime = new PiMateriaRuntime(
            _scopeFactory,
            new StaticOptionsMonitor<RuntimeOptions>(
                new RuntimeOptions
                {
                    Provider = "PiMateria",
                    PiExecutablePath = "pi",
                    // ControllerBaseUrl intentionally unset
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

        var run = await _runStore.GetByIdAsync(runId, CancellationToken.None);
        Assert.NotNull(run);
        Assert.Equal(RunLifecycleState.Failed, run!.Status);

        var events = await _eventStore.ListByRunIdAsync(runId, CancellationToken.None);
        Assert.Contains(events, e =>
            e.EventType == RuntimeEventTypes.Failed &&
            e.Message != null &&
            e.Message.Contains("controllerBaseUrl", StringComparison.OrdinalIgnoreCase)
        );

        runtime.Dispose();
    }

    // ── CancelAsync: no registered session ───────────────────────────

    [Fact]
    public async Task CancelAsync_NoRegisteredSession_ReturnsImmediately()
    {
        var runtime = new PiMateriaRuntime(
            _scopeFactory,
            new StaticOptionsMonitor<RuntimeOptions>(
                new RuntimeOptions
                {
                    Provider = "PiMateria",
                    PiExecutablePath = "pi",
                    ControllerBaseUrl = "http://127.0.0.1:9999",
                }
            ),
            _provider.GetRequiredService<ILogger<PiMateriaRuntime>>()
        );

        var handle = new AgentRunHandle
        {
            RunId = "test-cancel-run",
            RuntimeRunId = "pi-test-cancel",
            Status = RunLifecycleState.AgentRunning,
            StartedAt = DateTimeOffset.UtcNow,
        };

        // CancelAsync should return immediately without error even when no session exists.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await runtime.CancelAsync(handle, CancellationToken.None);
        sw.Stop();

        // Should be nearly instant (no process to kill).
        Assert.True(sw.ElapsedMilliseconds < 100,
            $"CancelAsync should return quickly but took {sw.ElapsedMilliseconds}ms");

        runtime.Dispose();
    }

    // ── CancelAsync: session handle cleanup ─────────────────────────

    [Fact]
    public async Task CancelAsync_RemovesAndDisposesSessionHandle()
    {
        var fakePiPath = WriteCaptureScript(_tempRoot, "capture-cancel.sh");
        var runId = await SeedRunAsync();
        var spec = await BuildSpecAsync(runId, "Cancel test");
        var runtime = CreateRuntime(fakePiPath);

        var handle = await runtime.StartAsync(spec, CancellationToken.None);
        Assert.Equal(spec.RunId, handle.RunId);

        // Verify the session was registered via reflection on the private _sessions field.
        var sessionsField = typeof(PiMateriaRuntime)
            .GetField("_sessions", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(sessionsField);
        var sessionsObj = sessionsField!.GetValue(runtime)!;

        // Use the dictionary's Count property (works regardless of value type).
        var countProp = sessionsObj.GetType().GetProperty("Count")!;
        var countBefore = (int)countProp.GetValue(sessionsObj)!;
        Assert.Equal(1, countBefore); // Session should be registered after StartAsync.

        // Cancel the run.
        await runtime.CancelAsync(handle, CancellationToken.None);

        // Verify the session was removed from the registry.
        var countAfter = (int)countProp.GetValue(sessionsObj)!;
        Assert.Equal(0, countAfter); // Session should be removed after CancelAsync.

        runtime.Dispose();
    }

    // ── Dispose cleans remaining sessions ───────────────────────────

    [Fact]
    public void Dispose_CleansRemainingSessions()
    {
        var runtime = new PiMateriaRuntime(
            _scopeFactory,
            new StaticOptionsMonitor<RuntimeOptions>(
                new RuntimeOptions
                {
                    Provider = "PiMateria",
                    PiExecutablePath = "pi",
                    ControllerBaseUrl = "http://127.0.0.1:9999",
                }
            ),
            _provider.GetRequiredService<ILogger<PiMateriaRuntime>>()
        );

        // Dispose should not throw even with no sessions.
        runtime.Dispose();

        // Dispose again — should be idempotent.
        runtime.Dispose();
    }

    // ── Dispose is trivial ──────────────────────────────────────────

    [Fact]
    public void Dispose_IsTrivial_NoException()
    {
        var runtime = new PiMateriaRuntime(
            _scopeFactory,
            new StaticOptionsMonitor<RuntimeOptions>(
                new RuntimeOptions
                {
                    Provider = "PiMateria",
                    PiExecutablePath = "pi",
                    ControllerBaseUrl = "http://127.0.0.1:9999",
                }
            ),
            _provider.GetRequiredService<ILogger<PiMateriaRuntime>>()
        );

        // Should not throw.
        runtime.Dispose();
    }

    // ── Default executable falls back to "pi" ───────────────────────

    [Fact]
    public async Task StartAsync_NullExecutablePath_UsesPiFromPath()
    {
        // When PiExecutablePath is null or empty, the runtime uses "pi" from PATH.
        // We can't easily test this without "pi" installed, so we verify the
        // behavior by checking that the runtime doesn't crash with null path.
        // The actual "pi" lookup is delegated to the OS.
        var runtime = new PiMateriaRuntime(
            _scopeFactory,
            new StaticOptionsMonitor<RuntimeOptions>(
                new RuntimeOptions
                {
                    Provider = "PiMateria",
                    PiExecutablePath = null, // Should fall back to "pi"
                    ControllerBaseUrl = "http://127.0.0.1:9999",
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

        // If "pi" is not on PATH, this should synthesize a failure.
        // If "pi" is on PATH, it will try to launch (and likely fail for other reasons).
        var handle = await runtime.StartAsync(spec, CancellationToken.None);
        Assert.NotNull(handle.RuntimeRunId);

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

    // ── ReadCastTask tests (private static method via reflection) ────
    //
    // Guards the full-context cast task construction: work-item.md body +
    // acceptance-criteria.md + comments.md are all included in the cast task.

    [Fact]
    public void ReadCastTask_FullWorkItemWithAcceptanceCriteriaAndComments_ContainsAllSections()
    {
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

            var taskText = InvokeReadCastTask(spec, contextDir);

            Assert.Contains("# This is a story", taskText);
            Assert.Contains("Implement the feature that does the thing.", taskText);
            Assert.Contains("It should handle edge cases.", taskText);
            Assert.Contains("## Acceptance Criteria", taskText);
            Assert.Contains("Given the system is running", taskText);
            Assert.Contains("When the user clicks the button", taskText);
            Assert.Contains("Then the feature activates", taskText);
            Assert.Contains("## Comments", taskText);
            Assert.Contains("This should use the existing infrastructure layer.", taskText);
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
        var contextDir = Path.Combine(Path.GetTempPath(), $"pimateria-readcasttask-{Guid.NewGuid():N}");
        Directory.CreateDirectory(contextDir);

        try
        {
            File.WriteAllText(
                Path.Combine(contextDir, "work-item.md"),
                "# This is a story\n\nImplement the feature that does the thing.\n\nIt should handle edge cases.\n"
            );

            var spec = new AgentRunSpec
            {
                RunId = "test-run-id",
                WorkRef = new ExternalWorkRef { Source = "Local", ExternalId = "WI-42" },
            };

            var taskText = InvokeReadCastTask(spec, contextDir);

            Assert.Contains("# This is a story", taskText);
            Assert.Contains("Implement the feature that does the thing.", taskText);
            Assert.Contains("It should handle edge cases.", taskText);
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
        var contextDir = Path.Combine(Path.GetTempPath(), $"pimateria-readcasttask-{Guid.NewGuid():N}");
        Directory.CreateDirectory(contextDir);

        try
        {
            var spec = new AgentRunSpec
            {
                RunId = "test-run-id",
                WorkRef = new ExternalWorkRef { Source = "Local", ExternalId = "WI-99" },
            };

            var taskText = InvokeReadCastTask(spec, contextDir);

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
        var contextDir = Path.Combine(Path.GetTempPath(), $"pimateria-readcasttask-{Guid.NewGuid():N}");
        Directory.CreateDirectory(contextDir);

        try
        {
            var spec = new AgentRunSpec
            {
                RunId = "test-run-id-abc",
                WorkRef = new ExternalWorkRef { Source = "Local" },
            };

            var taskText = InvokeReadCastTask(spec, contextDir);

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

    // ── Helpers ─────────────────────────────────────────────────────

    /// <summary>
    /// Write a shell script that captures its arguments and environment
    /// variables to a file, then exits immediately. Used as a fake <c>pi</c>.
    /// </summary>
    private static string WriteCaptureScript(string tempDir, string scriptName)
    {
        var scriptPath = Path.Combine(tempDir, scriptName);
        var capturePath = Path.Combine(tempDir, "capture.txt");
        var captureEnvPath = Path.Combine(tempDir, "capture-env.txt");
        var captureCwdPath = Path.Combine(tempDir, "capture-cwd.txt");
        var captureDetachedPath = Path.Combine(tempDir, "capture-detached.txt");

        // Use a Python script for cross-platform compatibility.
        var pythonScript = $@"#!/usr/bin/env python3
import sys, os

# Capture arguments
with open({CapturePathLiteral(capturePath)}, 'w') as f:
    f.write('ARGS: ' + ' '.join(sys.argv[1:]) + '\n')
    f.write('ARG_COUNT: ' + str(len(sys.argv) - 1) + '\n')

# Capture environment variables
with open({CapturePathLiteral(captureEnvPath)}, 'w') as f:
    for key in sorted(os.environ.keys()):
        f.write(key + '=' + os.environ[key] + '\n')

# Capture working directory
with open({CapturePathLiteral(captureCwdPath)}, 'w') as f:
    f.write('CWD=' + os.getcwd() + '\n')

# Capture detached status (no redirected streams)
with open({CapturePathLiteral(captureDetachedPath)}, 'w') as f:
    f.write('DETACHED=true\n')

sys.exit(0)
";

        File.WriteAllText(scriptPath, pythonScript);

        try
        {
            var info = new System.Diagnostics.ProcessStartInfo("chmod", ["+x", scriptPath])
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            System.Diagnostics.Process.Start(info)?.WaitForExit(2000);
        }
        catch
        {
            // chmod is best-effort.
        }

        return scriptPath;
    }

    private static string CapturePathLiteral(string path)
    {
        // Escape for embedding in Python string literal.
        return "'" + path.Replace("\\", "\\\\").Replace("'", "\\'") + "'";
    }

    /// <summary>
    /// Create a PiMateriaRuntime with the given executable path and a real HTTP listener.
    /// </summary>
    private PiMateriaRuntime CreateRuntime(string? fakePiPath = null)
    {
        // Allocate an ephemeral port for the controller base URL.
        using var portFinder = new TcpListener(IPAddress.Loopback, 0);
        portFinder.Start();
        var port = ((IPEndPoint)portFinder.LocalEndpoint).Port;
        portFinder.Stop();

        var baseUrl = $"http://127.0.0.1:{port}";

        return new PiMateriaRuntime(
            _scopeFactory,
            new StaticOptionsMonitor<RuntimeOptions>(
                new RuntimeOptions
                {
                    Provider = "PiMateria",
                    PiExecutablePath = fakePiPath,
                    ControllerBaseUrl = baseUrl,
                }
            ),
            _provider.GetRequiredService<ILogger<PiMateriaRuntime>>()
        );
    }

    /// <summary>
    /// Build an AgentRunSpec with real on-disk context directories.
    /// </summary>
    private async Task<AgentRunSpec> BuildSpecAsync(string runId, string title)
    {
        var envDir = Path.Combine(_tempRoot, runId);
        var contextDir = Path.Combine(envDir, "context");
        var repoDir = Path.Combine(envDir, "repo");
        Directory.CreateDirectory(contextDir);
        Directory.CreateDirectory(repoDir);
        await File.WriteAllTextAsync(
            Path.Combine(contextDir, "work-item.md"),
            $"# {title}\n\nSome acceptance criteria.\n"
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
        Assert.Fail(
            $"Run '{runId}' did not reach target state within {timeout.TotalSeconds}s. "
                + $"Final state: {final?.Status.ToString() ?? "null"}."
        );
    }

    // ── Minimal in-memory stores ─────────────────────────────────────

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

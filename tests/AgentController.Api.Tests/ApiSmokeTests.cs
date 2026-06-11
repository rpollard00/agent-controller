using AgentController.Api;
using AgentController.Application;
using AgentController.Infrastructure;
using AgentController.Infrastructure.Options;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentController.Api.Tests;

public class ApiSmokeTests
{
    [Fact]
    public void ApiLayer_ReferencesApplicationAndInfrastructure()
    {
        // Prove Api -> Application and Api -> Infrastructure dependencies are resolvable.
        var appType = typeof(IWorkSource);
        Assert.NotNull(appType);

        var infraType = typeof(NoOpWorkSource);
        Assert.NotNull(infraType);
    }

    [Fact]
    public void ApiHost_CanBuildServiceCollection()
    {
        // Prove the host builder doesn't throw on basic construction.
        var builder = WebApplication.CreateBuilder(Array.Empty<string>());
        var app = builder.Build();

        Assert.NotNull(app);
    }

    [Fact]
    public void PollingWorker_CanBeConstructed()
    {
        // Prove the polling worker can be constructed with all dependencies.
        var options = Options.Create(new AgentControllerOptions { WorkerId = "test-worker" });
        var loggerFactory = LoggerFactory.Create(b => b.AddConsole());

        // Use a simple wrapper to satisfy IOptionsMonitor<T>.
        var monitor = new TestOptionsMonitor<AgentControllerOptions>(options.Value);

        var worker = new PollingWorker(
            new NoOpWorkSource(),
            new NoOpSourceControlProvider(),
            new NoOpEnvironmentProvider(),
            new NoOpAgentRuntime(),
            monitor,
            loggerFactory.CreateLogger<PollingWorker>()
        );

        Assert.NotNull(worker);
    }

    [Fact]
    public void PollingWorker_IsDisabledByDefault()
    {
        // Prove the worker respects WorkerEnabled = false (the default).
        var options = new AgentControllerOptions { WorkerId = "test-worker" };

        Assert.False(options.WorkerEnabled);
    }

    [Fact]
    public void PollingWorker_ExtendsBackgroundService()
    {
        // Prove the worker is a BackgroundService, keeping the seam for a future split.
        var workerType = typeof(PollingWorker);
        Assert.True(typeof(BackgroundService).IsAssignableFrom(workerType));
    }

    /// <summary>
    /// Minimal IOptionsMonitor implementation for testing.
    /// </summary>
    private sealed class TestOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public TestOptionsMonitor(T currentValue)
        {
            CurrentValue = currentValue;
        }

        public T CurrentValue { get; }

        public T Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}

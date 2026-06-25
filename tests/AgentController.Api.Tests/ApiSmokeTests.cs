using AgentController.Api;
using AgentController.Application;
using AgentController.Application.Abstractions;
using AgentController.Infrastructure;
using AgentController.Infrastructure.Options;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
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
    public void ServiceCollection_AllNoOpProvidersResolve()
    {
        // Prove the DI container can resolve all port interfaces after
        // registering options and no-op providers through the canonical
        // extension methods. This is the key scaffold wiring test.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["agentController:workerId"] = "test-worker",
                    ["agentController:runRoot"] = "/tmp/runs",
                    ["persistence:provider"] = "Sqlite",
                    ["persistence:connectionString"] = "Data Source=test.db",
                    ["workSource:provider"] = "LocalFake",
                    ["sourceControl:provider"] = "LocalFake",
                    ["environmentProvider:provider"] = "LocalWorkspace",
                    ["runtime:provider"] = "NoOp",
                }
            )
            .Build();

        var services = new ServiceCollection();
        services.AddAgentControllerOptions(config);
        services.AddAgentControllerNoOpProviders();

        var provider = services.BuildServiceProvider();

        // All four application ports must be resolvable.
        var workSource = provider.GetRequiredService<IWorkSource>();
        Assert.IsType<NoOpWorkSource>(workSource);

        var sourceControl = provider.GetRequiredService<ISourceControlProvider>();
        Assert.IsType<NoOpSourceControlProvider>(sourceControl);

        var envProvider = provider.GetRequiredService<IEnvironmentProvider>();
        Assert.IsType<NoOpEnvironmentProvider>(envProvider);

        var runtime = provider.GetRequiredService<IAgentRuntime>();
        Assert.IsType<NoOpAgentRuntime>(runtime);
    }

    [Fact]
    public void FullHost_BuildsWithoutExceptions()
    {
        // Prove the full WebApplication can build (but not start) without exceptions
        // when using valid in-memory configuration and no-op providers.
        // Starting the host would bind ports, which is non-deterministic in a unit test.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["agentController:workerId"] = "host-test",
                    ["agentController:runRoot"] = "/tmp/runs",
                    ["persistence:provider"] = "Sqlite",
                    ["persistence:connectionString"] = "Data Source=host.db",
                    ["workSource:provider"] = "LocalFake",
                    ["sourceControl:provider"] = "LocalFake",
                    ["environmentProvider:provider"] = "LocalWorkspace",
                    ["runtime:provider"] = "NoOp",
                }
            )
            .Build();

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Testing",
            // Prevent loading files from disk.
            ContentRootPath = Path.GetTempPath(),
            WebRootPath = Path.GetTempPath(),
        });

        builder.Configuration.Sources.Clear();
        builder.Configuration.AddConfiguration(config);

        builder.Services.AddAgentControllerOptions(builder.Configuration);
        builder.Services.AddAgentControllerNoOpProviders();

        var app = builder.Build();

        app.MapGet("/health", () => Results.Ok(new { Status = "Healthy", Timestamp = DateTimeOffset.UtcNow }));

        Assert.NotNull(app);

        // Verify all port interfaces can be resolved from the built host.
        var services = app.Services;
        Assert.NotNull(services.GetRequiredService<IWorkSource>());
        Assert.NotNull(services.GetRequiredService<ISourceControlProvider>());
        Assert.NotNull(services.GetRequiredService<IEnvironmentProvider>());
        Assert.NotNull(services.GetRequiredService<IAgentRuntime>());
    }

    [Fact]
    public void PollingWorker_CanBeConstructed()
    {
        // Prove the polling worker can be constructed with its Phase 1 dependencies.
        var options = Options.Create(new AgentControllerOptions { WorkerId = "test-worker" });
        var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
        var monitor = new TestOptionsMonitor<AgentControllerOptions>(options.Value);
        var workSourceMonitor = new TestOptionsMonitor<WorkSourceOptionsView>(new WorkSourceOptionsView());

        // Build a service provider to get an IServiceScopeFactory.
        var services = new ServiceCollection();
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        var worker = new PollingWorker(
            scopeFactory,
            monitor,
            workSourceMonitor,
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

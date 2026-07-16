using AgentController.Application;
using AgentController.Domain;
using AgentController.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgentController.Infrastructure.Tests;

public sealed class ManagedLifecycleProfileTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(),
        $"agent-controller-managed-lifecycle-{Guid.NewGuid():N}.db"
    );

    [Fact]
    public async Task Lifecycle_UsesManagedBoardStatesInsteadOfAppsettingsStates()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["agentController:workerId"] = "managed-lifecycle-test",
                    ["agentController:runRoot"] = "/tmp/managed-lifecycle-test",
                    ["persistence:provider"] = "Sqlite",
                    ["persistence:connectionString"] = $"Data Source={_databasePath}",
                    ["workSource:provider"] = "LocalFake",
                    ["workSource:activeState"] = "Configured Active",
                    ["workSource:completedState"] = "Configured Done",
                    ["environmentProvider:provider"] = "NoOp",
                    ["runtime:provider"] = "NoOp",
                }
            )
            .Build();
        var services = new ServiceCollection();
        services.AddSilentLogging();
        services.AddAgentControllerOptions(configuration);
        services.AddAgentControllerDbContext(configuration);
        services.AddAgentControllerRepositories();
        services.AddAgentControllerLifecycleService();
        services.AddApplicationHandlers();
        services.AddAgentControllerNoOpProviders();
        services.AddAgentControllerLocalFakeWorkSource();

        await using var serviceProvider = services.BuildServiceProvider();
        await using var scope = serviceProvider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentControllerDbContext>();
        await db.Database.EnsureCreatedAsync();

        var environmentStore =
            scope.ServiceProvider.GetRequiredService<IWorkSourceEnvironmentStore>();
        Assert.True(
            await environmentStore.CreateAsync(
                new WorkSourceEnvironmentProfile
                {
                    Key = "managed-ado",
                    DisplayName = "Managed ADO",
                    Enabled = true,
                    Provider = "AzureDevOpsBoards",
                    TagPrefix = "agent",
                    OrganizationUrl = "https://dev.azure.com/example",
                    Project = "Example",
                    ActiveState = "Managed Active",
                    CompletedState = "Managed Done",
                    PersonalAccessTokenReference = Domain.Secrets.SecretReference.ByName("MANAGED_LIFECYCLE_TEST_PAT"),
                },
                CancellationToken.None
            )
        );

        var workItemStore = scope.ServiceProvider.GetRequiredService<IWorkItemStore>();
        var workItem = await workItemStore.UpsertAsync(
            new WorkCandidate
            {
                Id = "managed-state-item",
                ExternalId = "200",
                Source = "AzureDevOpsBoards",
                Title = "Managed state projection",
                RepoKey = "example",
                Status = "New",
                SourceMetadata = new Dictionary<string, string>
                {
                    ["revision"] = "1",
                    ["workSourceEnvironmentKey"] = "managed-ado",
                },
            },
            CancellationToken.None
        );
        var lifecycle = scope.ServiceProvider.GetRequiredService<IRunLifecycleService>();
        var run = await lifecycle.CreateRunForWorkItemAsync(
            workItem.Id,
            "worker-1",
            CancellationToken.None
        );

        await AdvanceToAsync(
            lifecycle,
            run.RunId,
            RunLifecycleState.AwaitingResult,
            CancellationToken.None
        );
        Assert.Equal(
            "Managed Active",
            (await workItemStore.GetByIdAsync(workItem.Id, CancellationToken.None))?.Status
        );

        await lifecycle.IngestRuntimeEventAsync(
            new RuntimeEvent
            {
                EventId = "managed-state-completed",
                RunId = run.RunId,
                EventType = RuntimeEventTypes.Completed,
                OccurredAt = DateTimeOffset.UtcNow,
                Payload = new Dictionary<string, object?>
                {
                    ["outcome"] = CompletionOutcomes.PatchCreated,
                },
            },
            CancellationToken.None
        );

        Assert.Equal(
            "Managed Done",
            (await workItemStore.GetByIdAsync(workItem.Id, CancellationToken.None))?.Status
        );
    }

    public void Dispose()
    {
        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }
    }

    private static async Task AdvanceToAsync(
        IRunLifecycleService lifecycle,
        string runId,
        RunLifecycleState target,
        CancellationToken cancellationToken
    )
    {
        var states = new[]
        {
            RunLifecycleState.EnvironmentProvisioning,
            RunLifecycleState.EnvironmentReady,
            RunLifecycleState.RepositoryCloning,
            RunLifecycleState.RepositoryReady,
            RunLifecycleState.ContextInjected,
            RunLifecycleState.AgentStarting,
            RunLifecycleState.AgentRunning,
            RunLifecycleState.AwaitingResult,
        };

        foreach (var state in states)
        {
            await lifecycle.TransitionAsync(runId, state, cancellationToken);
            if (state == target)
            {
                return;
            }
        }
    }
}

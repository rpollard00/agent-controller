using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentController.Application;
using AgentController.Domain;
using AgentController.Infrastructure.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentController.Api.Tests;

/// <summary>End-to-end coverage for the Web UI runs dashboard API.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "IAsyncLifetime.DisposeAsync disposes all owned fields."
)]
public sealed class WebUiRunsEndpointTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Baseline = new(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);

    private string _databasePath = null!;
    private RunsApiFactory _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _databasePath = Path.Combine(
            Path.GetTempPath(),
            $"agent-controller-webui-runs-{Guid.NewGuid():N}.db"
        );
        _factory = new RunsApiFactory(_databasePath);

        using var scope = _factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AgentControllerDbContext>();
        await database.Database.EnsureCreatedAsync();

        _client = _factory.CreateClient();
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();

        DeleteDatabaseFile(_databasePath);
        DeleteDatabaseFile($"{_databasePath}-shm");
        DeleteDatabaseFile($"{_databasePath}-wal");
        return Task.CompletedTask;
    }

    [Fact]
    public async Task GetRuns_ReturnsEnrichedRunAndSoakCardsInLatestActivityOrder()
    {
        AgentRunHandle executingRun;
        AgentRunHandle attentionRun;
        AgentRunHandle completedRun;
        ReworkFeedback feedback;

        using (var scope = _factory.Services.CreateScope())
        {
            var services = scope.ServiceProvider;
            var repositoryStore = services.GetRequiredService<IRepositoryStore>();
            var workItemStore = services.GetRequiredService<IWorkItemStore>();
            var runStore = services.GetRequiredService<IAgentRunStore>();
            var lifecycleEventStore = services.GetRequiredService<ILifecycleEventStore>();
            var feedbackStore = services.GetRequiredService<IReworkFeedbackStore>();

            Assert.True(
                await repositoryStore.CreateAsync(
                    new RepositoryProfile
                    {
                        Key = "dashboard-repo",
                        CloneUrl = "https://git.example.test/org/dashboard-repo.git",
                        WebUrl = "https://git.example.test/org/dashboard-repo",
                    },
                    CancellationToken.None
                )
            );

            var workItem = await workItemStore.UpsertAsync(
                new WorkCandidate
                {
                    ExternalId = "work-42",
                    ExternalUrl = "https://work.example.test/items/42",
                    RepoKey = "dashboard-repo",
                    Title = "Build the runs dashboard",
                    Status = "Active",
                    Source = "AzureDevOpsBoards",
                },
                CancellationToken.None
            );

            executingRun = await CreateRunAsync(
                runStore,
                workItem.Id,
                RunLifecycleState.AgentRunning,
                runAttempt: 2
            );
            attentionRun = await CreateRunAsync(
                runStore,
                workItem.Id,
                RunLifecycleState.Failed,
                runAttempt: 3
            );
            completedRun = await CreateRunAsync(
                runStore,
                workItem.Id,
                RunLifecycleState.Completed,
                runAttempt: 1
            );

            await AppendEventAsync(
                lifecycleEventStore,
                completedRun.RunId,
                "controller.completed",
                "Run completed",
                Baseline
            );
            await AppendEventAsync(
                lifecycleEventStore,
                executingRun.RunId,
                "runtime.progress",
                "Implementing dashboard cards",
                Baseline.AddMinutes(1)
            );
            await AppendEventAsync(
                lifecycleEventStore,
                attentionRun.RunId,
                "controller.failed",
                "Run needs attention",
                Baseline.AddMinutes(3)
            );

            feedback = await feedbackStore.UpsertAsync(
                attentionRun.RunId,
                "pull-request-42",
                "feedback-bundle-42",
                "[]",
                threadCount: 3,
                firstQualifyingCommentAt: Baseline.AddMinutes(-1),
                lastQualifyingCommentAt: Baseline.AddMinutes(2),
                ReworkFeedbackStatus.Watching,
                CancellationToken.None
            );
        }

        using var response = await _client.GetAsync("/api/webui/runs");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Array, payload.ValueKind);
        var cards = payload.EnumerateArray().ToArray();
        Assert.Equal(4, cards.Length);
        Assert.All(cards, AssertRunCardShape);
        Assert.Equal(
            [attentionRun.RunId, feedback.Id, executingRun.RunId, completedRun.RunId],
            cards.Select(card => card.GetProperty("id").GetString()!).ToArray()
        );

        var cardsById = cards.ToDictionary(
            card => card.GetProperty("id").GetString()!,
            StringComparer.Ordinal
        );

        var executingCard = cardsById[executingRun.RunId];
        Assert.Equal("run", executingCard.GetProperty("kind").GetString());
        Assert.Equal("AgentRunning", executingCard.GetProperty("status").GetString());
        Assert.Equal("executing", executingCard.GetProperty("category").GetString());
        Assert.Equal(
            "Build the runs dashboard",
            executingCard.GetProperty("workItemTitle").GetString()
        );
        Assert.Equal(
            "https://work.example.test/items/42",
            executingCard.GetProperty("workItemUrl").GetString()
        );
        Assert.Equal("AzureDevOpsBoards", executingCard.GetProperty("workItemSource").GetString());
        Assert.Equal("dashboard-repo", executingCard.GetProperty("repoKey").GetString());
        Assert.Equal(
            "https://git.example.test/org/dashboard-repo",
            executingCard.GetProperty("repositoryUrl").GetString()
        );
        Assert.Equal("PiMateria", executingCard.GetProperty("runtimeType").GetString());
        Assert.Equal(2, executingCard.GetProperty("runAttempt").GetInt32());
        Assert.Equal("runtime.progress", executingCard.GetProperty("lastEventType").GetString());
        Assert.Equal(
            "Implementing dashboard cards",
            executingCard.GetProperty("lastEventMessage").GetString()
        );
        Assert.Equal(
            Baseline.AddMinutes(1),
            executingCard.GetProperty("lastEventAt").GetDateTimeOffset()
        );

        Assert.Equal(
            "attention",
            cardsById[attentionRun.RunId].GetProperty("category").GetString()
        );
        Assert.Equal(
            "completed",
            cardsById[completedRun.RunId].GetProperty("category").GetString()
        );

        var soakCard = cardsById[feedback.Id];
        Assert.Equal("rework-soak", soakCard.GetProperty("kind").GetString());
        Assert.Equal("Rework feedback soaking", soakCard.GetProperty("status").GetString());
        Assert.Equal("pending", soakCard.GetProperty("category").GetString());
        Assert.Equal("rework.feedback.soaking", soakCard.GetProperty("lastEventType").GetString());
        Assert.Equal(
            "3 feedback threads awaiting soak",
            soakCard.GetProperty("lastEventMessage").GetString()
        );
        Assert.Equal(
            Baseline.AddMinutes(2),
            soakCard.GetProperty("lastEventAt").GetDateTimeOffset()
        );
    }

    private static Task<AgentRunHandle> CreateRunAsync(
        IAgentRunStore runStore,
        string workItemId,
        RunLifecycleState status,
        int runAttempt
    ) =>
        runStore.CreateAsync(
            new CreateRunRequest
            {
                WorkItemId = workItemId,
                WorkerId = "runs-endpoint-test",
                RuntimeType = "PiMateria",
                InitialStatus = status,
                RunAttempt = runAttempt,
            },
            CancellationToken.None
        );

    private static Task AppendEventAsync(
        ILifecycleEventStore eventStore,
        string runId,
        string eventType,
        string message,
        DateTimeOffset createdAt
    ) =>
        eventStore.AppendAsync(
            new LifecycleEvent
            {
                RunId = runId,
                EventType = eventType,
                Message = message,
                CreatedAt = createdAt,
            },
            CancellationToken.None
        );

    private static void AssertRunCardShape(JsonElement card)
    {
        string[] propertyNames =
        [
            "id",
            "kind",
            "status",
            "category",
            "workItemTitle",
            "workItemUrl",
            "workItemSource",
            "repoKey",
            "repositoryUrl",
            "runtimeType",
            "runAttempt",
            "lastEventType",
            "lastEventMessage",
            "lastEventAt",
            "createdAt",
            "updatedAt",
        ];

        foreach (var propertyName in propertyNames)
        {
            Assert.True(
                card.TryGetProperty(propertyName, out _),
                $"Run card payload is missing '{propertyName}'."
            );
        }
    }

    private static void DeleteDatabaseFile(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private sealed class RunsApiFactory(string databasePath) : SilentWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration(
                (_, configuration) =>
                    configuration.AddInMemoryCollection(
                        new Dictionary<string, string?>
                        {
                            ["agentController:workerEnabled"] = "false",
                            ["workSource:provider"] = "LocalFake",
                            ["sourceControl:provider"] = "NoOp",
                            ["environmentProvider:provider"] = "NoOp",
                            ["runtime:provider"] = "NoOp",
                            ["feedback:enabled"] = "false",
                            ["feedback:provider"] = "None",
                        }
                    )
            );
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<AgentControllerDbContext>();
                services.RemoveAll<DbContextOptions<AgentControllerDbContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<AgentControllerDbContext>>();
                services.AddDbContext<AgentControllerDbContext>(options =>
                    options.UseSqlite(
                        $"Data Source={databasePath}",
                        sqlite => sqlite.MigrationsAssembly("AgentController.Migrations")
                    )
                );
            });
        }
    }
}

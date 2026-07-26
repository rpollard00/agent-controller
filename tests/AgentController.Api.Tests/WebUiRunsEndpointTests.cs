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
        Assert.Equal(
            "ReeseProjecto LocalWorkspace",
            executingCard.GetProperty("runtimeProfileName").GetString()
        );
        Assert.Equal(
            "LocalWorkspace",
            executingCard.GetProperty("environmentProviderType").GetString()
        );
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
        Assert.Equal("PiMateria", soakCard.GetProperty("runtimeType").GetString());
        Assert.Equal(
            "ReeseProjecto LocalWorkspace",
            soakCard.GetProperty("runtimeProfileName").GetString()
        );
        Assert.Equal(
            "LocalWorkspace",
            soakCard.GetProperty("environmentProviderType").GetString()
        );
        Assert.Equal("revival", soakCard.GetProperty("requestMode").GetString());
        Assert.Equal("watching", soakCard.GetProperty("feedbackStatus").GetString());
        Assert.Equal(JsonValueKind.Null, soakCard.GetProperty("cycleNumber").ValueKind);
        Assert.Equal(JsonValueKind.Null, soakCard.GetProperty("consumingRunId").ValueKind);
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

    [Fact]
    public async Task GetRunsAndRunDetail_ExposeMaterializedAssistanceLineage()
    {
        AgentRunHandle consumingRun;
        WorkCandidate assistanceStory;
        PullRequestReference pullRequest;

        using (var scope = _factory.Services.CreateScope())
        {
            var services = scope.ServiceProvider;
            var workItemStore = services.GetRequiredService<IWorkItemStore>();
            var runStore = services.GetRequiredService<IAgentRunStore>();
            var feedbackStore = services.GetRequiredService<IReworkFeedbackStore>();
            var cycleStore = services.GetRequiredService<IReworkCycleStore>();

            assistanceStory = await workItemStore.UpsertAsync(
                new WorkCandidate
                {
                    ExternalId = "8042",
                    ExternalUrl = "https://dev.azure.test/workitems/8042",
                    RepoKey = "dashboard-repo",
                    Title = "Assist existing pull request 42",
                    Status = "Active",
                    Source = "AzureDevOpsBoards",
                },
                CancellationToken.None
            );
            pullRequest = new PullRequestReference
            {
                EnvironmentKey = "ado-prod",
                RepositoryKey = "dashboard-repo",
                PullRequestId = "42",
                PullRequestUrl = "https://dev.azure.test/repo/pullrequest/42",
                SourceBranch = "refs/heads/contributor/change",
                TargetBranch = "refs/heads/main",
                SourceCommitSha = "abc123",
            };
            var feedback = await feedbackStore.UpsertAsync(
                new ReworkFeedbackUpsertRequest
                {
                    RequestMode = ReworkRequestMode.Assistance,
                    PullRequest = pullRequest,
                    FeedbackBundleId = "assistance-bundle-42",
                    FeedbackBundleJson = "[]",
                    ThreadCount = 0,
                    FirstQualifyingCommentAt = Baseline,
                    LastQualifyingCommentAt = Baseline,
                    Status = ReworkFeedbackStatus.Soaked,
                    CorrelationId = "assistance-correlation-42",
                },
                CancellationToken.None
            );
            await feedbackStore.RecordAssistanceStoryAsync(
                feedback.Id,
                new AssistanceStoryReceipt
                {
                    CorrelationId = feedback.CorrelationId!,
                    WorkItemId = assistanceStory.Id,
                    ExternalId = assistanceStory.ExternalId,
                    Url = assistanceStory.ExternalUrl,
                },
                CancellationToken.None
            );
            var cycle = await cycleStore.CreateAsync(
                new ReworkCycleCreateRequest
                {
                    RequestMode = ReworkRequestMode.Assistance,
                    PullRequest = pullRequest,
                    WorkItemId = assistanceStory.Id,
                    FeedbackBundleId = feedback.FeedbackBundleId,
                    FeedbackBundleJson = feedback.FeedbackBundleJson,
                    CorrelationId = feedback.CorrelationId,
                    BranchName = pullRequest.SourceBranch,
                    PullRequestUrl = pullRequest.PullRequestUrl,
                    BaseCommitSha = pullRequest.SourceCommitSha,
                },
                CancellationToken.None
            );
            consumingRun = await CreateRunAsync(
                runStore,
                assistanceStory.Id,
                RunLifecycleState.AgentRunning,
                runAttempt: 1
            );
            await cycleStore.MarkConsumedAsync(
                cycle.Id,
                consumingRun.RunId,
                CancellationToken.None
            );
            await feedbackStore.MarkMaterializedAsync(feedback.Id, CancellationToken.None);
        }

        using var cardsResponse = await _client.GetAsync("/api/webui/runs");
        cardsResponse.EnsureSuccessStatusCode();
        var cards = await cardsResponse.Content.ReadFromJsonAsync<JsonElement>();
        var runCard = cards
            .EnumerateArray()
            .Single(card => card.GetProperty("id").GetString() == consumingRun.RunId);
        AssertAssistanceTracking(runCard, consumingRun.RunId, assistanceStory, pullRequest);

        using var detailResponse = await _client.GetAsync($"/runs/{consumingRun.RunId}");
        detailResponse.EnsureSuccessStatusCode();
        var detail = await detailResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(consumingRun.RunId, detail.GetProperty("runId").GetString());
        AssertAssistanceTracking(detail, consumingRun.RunId, assistanceStory, pullRequest);
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
                RuntimeProfileName = "ReeseProjecto LocalWorkspace",
                EnvironmentProviderType = "LocalWorkspace",
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

    private static void AssertAssistanceTracking(
        JsonElement result,
        string consumingRunId,
        WorkCandidate story,
        PullRequestReference pullRequest
    )
    {
        Assert.Equal("assistance", result.GetProperty("requestMode").GetString());
        Assert.Equal(1, result.GetProperty("cycleNumber").GetInt32());
        Assert.Equal(story.Id, result.GetProperty("assistanceStoryWorkItemId").GetString());
        Assert.Equal(story.ExternalId, result.GetProperty("assistanceStoryExternalId").GetString());
        Assert.Equal(story.ExternalUrl, result.GetProperty("assistanceStoryUrl").GetString());
        Assert.Equal("materialized", result.GetProperty("feedbackStatus").GetString());
        Assert.Equal("consumed", result.GetProperty("cycleStatus").GetString());
        Assert.Equal(consumingRunId, result.GetProperty("consumingRunId").GetString());

        var pullRequestResult = result.GetProperty("pullRequest");
        Assert.Equal(pullRequest.CanonicalKey, pullRequestResult.GetProperty("canonicalKey").GetString());
        Assert.Equal(pullRequest.EnvironmentKey, pullRequestResult.GetProperty("environmentKey").GetString());
        Assert.Equal(pullRequest.RepositoryKey, pullRequestResult.GetProperty("repositoryKey").GetString());
        Assert.Equal(pullRequest.PullRequestId, pullRequestResult.GetProperty("pullRequestId").GetString());
        Assert.Equal(pullRequest.PullRequestUrl, pullRequestResult.GetProperty("pullRequestUrl").GetString());
        Assert.Equal(pullRequest.SourceBranch, pullRequestResult.GetProperty("sourceBranch").GetString());
        Assert.Equal(pullRequest.TargetBranch, pullRequestResult.GetProperty("targetBranch").GetString());
        Assert.Equal(pullRequest.SourceCommitSha, pullRequestResult.GetProperty("sourceCommitSha").GetString());
    }

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
            "runtimeProfileName",
            "environmentProviderType",
            "runAttempt",
            "requestMode",
            "pullRequest",
            "cycleNumber",
            "assistanceStoryWorkItemId",
            "assistanceStoryExternalId",
            "assistanceStoryUrl",
            "feedbackStatus",
            "cycleStatus",
            "consumingRunId",
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

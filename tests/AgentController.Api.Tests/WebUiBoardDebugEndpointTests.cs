using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentController.Application;
using AgentController.Application.Abstractions;
using AgentController.Application.Queries;
using AgentController.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentController.Api.Tests;

public sealed class WebUiBoardDebugEndpointTests : IDisposable
{
    private readonly BoardDebugApiFactory _factory = new();
    private readonly HttpClient _client;

    public WebUiBoardDebugEndpointTests()
    {
        _client = _factory.CreateClient();
    }

    [Fact]
    public async Task List_UsesDefaultsAndReturnsExpectedOperatorSafeShape()
    {
        using var response = await _client.GetAsync("/api/webui/debug/board-items");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, payload.GetProperty("page").GetInt32());
        Assert.Equal(50, payload.GetProperty("pageSize").GetInt32());
        Assert.Equal(73, payload.GetProperty("total").GetInt32());
        Assert.Equal(JsonValueKind.String, payload.GetProperty("observedAt").ValueKind);
        Assert.Equal(2, payload.GetProperty("sourceOptions").GetArrayLength());
        Assert.Equal("Primary Boards", payload.GetProperty("sourceOptions")[0].GetProperty("displayName").GetString());
        Assert.Equal(1, payload.GetProperty("items").GetArrayLength());
        Assert.Equal("wi-42", payload.GetProperty("items")[0].GetProperty("id").GetString());
        Assert.Equal("missingTags", payload.GetProperty("items")[0].GetProperty("match").GetString());
        Assert.Equal("Unavailable", payload.GetProperty("failures")[0].GetProperty("message").GetString());

        var query = Assert.Single(_factory.ListHandler.Queries);
        Assert.Null(query.WorkSourceEnvironmentKey);
        Assert.False(query.IncludeTerminal);
        Assert.Equal(1, query.Page);
        Assert.Equal(50, query.PageSize);

        var json = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("connectionKey", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("credential", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task List_ForwardsCanonicalFilterTerminalToggleAndPagination()
    {
        using var response = await _client.GetAsync(
            "/api/webui/debug/board-items?workSourceEnvironmentKey=BOARDS.SECONDARY&includeTerminal=true&page=3&pageSize=25");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var query = Assert.Single(_factory.ListHandler.Queries);
        Assert.Equal("boards.secondary", query.WorkSourceEnvironmentKey);
        Assert.True(query.IncludeTerminal);
        Assert.Equal(3, query.Page);
        Assert.Equal(25, query.PageSize);
    }

    [Theory]
    [InlineData("?page=0", "page")]
    [InlineData("?pageSize=101", "pageSize")]
    [InlineData("?pageSize=0", "pageSize")]
    [InlineData("?workSourceEnvironmentKey=missing", "workSourceEnvironmentKey")]
    [InlineData("?workSourceEnvironmentKey=%20", "workSourceEnvironmentKey")]
    public async Task List_RejectsInvalidInputWithoutRunningDiscovery(string query, string field)
    {
        using var response = await _client.GetAsync($"/api/webui/debug/board-items{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _));
        Assert.Empty(_factory.ListHandler.Queries);
    }

    [Fact]
    public async Task Detail_IsLazyAndReturnsDiagnosticsWithoutProfileSecrets()
    {
        Assert.Empty(_factory.DetailHandler.Queries);

        using var response = await _client.GetAsync(
            "/api/webui/debug/board-items/BOARDS.PRIMARY/wi%2042");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var query = Assert.Single(_factory.DetailHandler.Queries);
        Assert.Equal("boards.primary", query.WorkSourceEnvironmentKey);
        Assert.Equal("wi 42", query.ItemId);

        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("https://boards.example.test/items/42", payload.GetProperty("url").GetString());
        Assert.True(payload.GetProperty("eligible").GetBoolean());
        Assert.Equal("repo:widgets", payload.GetProperty("recognizedRepositoryTags")[0].GetString());
        Assert.Equal("agent-ready-rework", payload.GetProperty("recognizedReadyReworkTag").GetString());
        Assert.Equal("Ready marker", payload.GetProperty("checks")[0].GetProperty("label").GetString());

        var json = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("connection", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Detail_ReturnsNotFoundAndValidatesEnvironment()
    {
        using var missing = await _client.GetAsync(
            "/api/webui/debug/board-items/boards.primary/not-found");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        using var unknownSource = await _client.GetAsync(
            "/api/webui/debug/board-items/unknown/wi-42");
        Assert.Equal(HttpStatusCode.BadRequest, unknownSource.StatusCode);
        Assert.Single(_factory.DetailHandler.Queries);
    }

    [Fact]
    public async Task NonAzureWorkSource_MapsEndpointsWithoutBreakingTheHost()
    {
        using var factory = new NonAzureApiFactory();
        using var client = factory.CreateClient();

        using var health = await client.GetAsync("/");
        using var debug = await client.GetAsync("/api/webui/debug/board-items");

        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal(HttpStatusCode.OK, debug.StatusCode);
        var payload = await debug.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, payload.GetProperty("total").GetInt32());
        Assert.Empty(payload.GetProperty("items").EnumerateArray());
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private sealed class NonAzureApiFactory : SilentWebApplicationFactory
    {
        private readonly string _databasePath = Path.Combine(
            Path.GetTempPath(),
            $"agent-controller-board-debug-noop-{Guid.NewGuid():N}.db");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["agentController:workerEnabled"] = "false",
                    ["persistence:connectionString"] = $"Data Source={_databasePath}",
                    ["workSource:provider"] = "LocalFake",
                    ["feedback:enabled"] = "false",
                    ["feedback:provider"] = "None",
                }));
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && File.Exists(_databasePath))
            {
                File.Delete(_databasePath);
            }
        }
    }

    private sealed class BoardDebugApiFactory : SilentWebApplicationFactory
    {
        private readonly string _databasePath = Path.Combine(
            Path.GetTempPath(),
            $"agent-controller-board-debug-{Guid.NewGuid():N}.db");

        public RecordingListHandler ListHandler { get; } = new();
        public RecordingDetailHandler DetailHandler { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["agentController:workerEnabled"] = "false",
                    ["persistence:connectionString"] = $"Data Source={_databasePath}",
                    ["workSource:provider"] = "LocalFake",
                    ["feedback:enabled"] = "false",
                    ["feedback:provider"] = "None",
                }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IQueryHandler<ListWorkSourceEnvironmentsQuery, IReadOnlyList<WorkSourceEnvironmentProfile>>>();
                services.RemoveAll<IQueryHandler<ListBoardItemDiagnosticsQuery, BoardItemDiagnosticsPage>>();
                services.RemoveAll<IQueryHandler<GetBoardItemDiagnosticsQuery, BoardItemDiagnosticDetail?>>();
                services.AddSingleton<IQueryHandler<ListWorkSourceEnvironmentsQuery, IReadOnlyList<WorkSourceEnvironmentProfile>>>(
                    new EnvironmentHandler());
                services.AddSingleton<IQueryHandler<ListBoardItemDiagnosticsQuery, BoardItemDiagnosticsPage>>(ListHandler);
                services.AddSingleton<IQueryHandler<GetBoardItemDiagnosticsQuery, BoardItemDiagnosticDetail?>>(DetailHandler);
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && File.Exists(_databasePath))
            {
                File.Delete(_databasePath);
            }
        }
    }

    private sealed class EnvironmentHandler : IQueryHandler<ListWorkSourceEnvironmentsQuery, IReadOnlyList<WorkSourceEnvironmentProfile>>
    {
        public Task<IReadOnlyList<WorkSourceEnvironmentProfile>> ExecuteAsync(
            ListWorkSourceEnvironmentsQuery query,
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<WorkSourceEnvironmentProfile>>(
            [
                new()
                {
                    Key = "boards.primary",
                    DisplayName = "Primary Boards",
                    ConnectionKey = "credential-bearing-connection",
                    Project = "Widgets",
                },
                new()
                {
                    Key = "boards.secondary",
                    DisplayName = "Secondary Boards",
                    Enabled = false,
                    ConnectionKey = "other-secret-connection",
                    Project = "Gadgets",
                },
            ]);
    }

    public sealed class RecordingListHandler : IQueryHandler<ListBoardItemDiagnosticsQuery, BoardItemDiagnosticsPage>
    {
        public List<ListBoardItemDiagnosticsQuery> Queries { get; } = [];

        public Task<BoardItemDiagnosticsPage> ExecuteAsync(
            ListBoardItemDiagnosticsQuery query,
            CancellationToken cancellationToken)
        {
            Queries.Add(query);
            return Task.FromResult(new BoardItemDiagnosticsPage
            {
                Page = query.Page,
                PageSize = query.PageSize,
                Total = 73,
                Items =
                [
                    new()
                    {
                        Id = "wi-42",
                        Title = "Fix tagged pickup",
                        Url = "https://boards.example.test/items/42",
                        Project = "Widgets",
                        WorkSourceEnvironmentKey = "boards.primary",
                        State = "Active",
                        Match = BoardItemMatchResult.MissingTags,
                    },
                ],
                Failures =
                [
                    new()
                    {
                        WorkSourceEnvironmentKey = "boards.secondary",
                        Project = "Gadgets",
                        Message = "Unavailable",
                    },
                ],
            });
        }
    }

    public sealed class RecordingDetailHandler : IQueryHandler<GetBoardItemDiagnosticsQuery, BoardItemDiagnosticDetail?>
    {
        public List<GetBoardItemDiagnosticsQuery> Queries { get; } = [];

        public Task<BoardItemDiagnosticDetail?> ExecuteAsync(
            GetBoardItemDiagnosticsQuery query,
            CancellationToken cancellationToken)
        {
            Queries.Add(query);
            if (query.ItemId == "not-found")
            {
                return Task.FromResult<BoardItemDiagnosticDetail?>(null);
            }

            return Task.FromResult<BoardItemDiagnosticDetail?>(new BoardItemDiagnosticDetail
            {
                Id = query.ItemId,
                Title = "Fix tagged pickup",
                Url = "https://boards.example.test/items/42",
                Project = "Widgets",
                WorkSourceEnvironmentKey = query.WorkSourceEnvironmentKey,
                RepositoryKey = "widgets",
                State = "Active",
                Tags = ["repo:widgets", "agent-ready-rework"],
                Match = BoardItemMatchResult.Eligible,
                Eligible = true,
                Checks =
                [
                    new()
                    {
                        Code = "ready-marker",
                        Label = "Ready marker",
                        Passed = true,
                        Reason = "A recognized marker is present.",
                    },
                ],
                RecognizedRepositoryTags = ["repo:widgets"],
                RecognizedReadyTag = "agent-ready",
                RecognizedReadyReworkTag = "agent-ready-rework",
            });
        }
    }
}

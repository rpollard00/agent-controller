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

public sealed class WebUiPullRequestDebugEndpointTests : IDisposable
{
    private readonly PullRequestDebugApiFactory _factory = new();
    private readonly HttpClient _client;

    public WebUiPullRequestDebugEndpointTests() => _client = _factory.CreateClient();

    [Fact]
    public async Task List_UsesDefaultsAndReturnsSafePagedShapeWithoutLoadingDetail()
    {
        using var response = await _client.GetAsync("/api/webui/debug/pull-requests");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, payload.GetProperty("page").GetInt32());
        Assert.Equal(50, payload.GetProperty("pageSize").GetInt32());
        Assert.Equal(61, payload.GetProperty("total").GetInt32());
        Assert.Equal(JsonValueKind.String, payload.GetProperty("observedAt").ValueKind);
        Assert.Equal("Azure Repos", payload.GetProperty("sourceOptions")[0].GetProperty("name").GetString());
        Assert.Equal("both", payload.GetProperty("items")[0].GetProperty("request").GetString());
        Assert.True(payload.GetProperty("items")[0].GetProperty("eligible").GetBoolean());
        Assert.False(payload.GetProperty("items")[1].GetProperty("eligible").GetBoolean());
        Assert.Equal("Unavailable", payload.GetProperty("failures")[0].GetProperty("message").GetString());
        var query = Assert.Single(_factory.ListHandler.Queries);
        Assert.Null(query.SourceControlEnvironmentKey);
        Assert.False(query.IncludeInactive);
        Assert.Equal(1, query.Page);
        Assert.Equal(50, query.PageSize);
        Assert.Empty(_factory.DetailHandler.Queries);

        var json = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("personalAccessToken", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task List_ForwardsCanonicalEnvironmentInactiveToggleAndPagination()
    {
        using var response = await _client.GetAsync(
            "/api/webui/debug/pull-requests?sourceControlEnvironmentKey=ADO%20PRIMARY&includeInactive=true&page=2&pageSize=25");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var query = Assert.Single(_factory.ListHandler.Queries);
        Assert.Equal("ado primary", query.SourceControlEnvironmentKey);
        Assert.True(query.IncludeInactive);
        Assert.Equal(2, query.Page);
        Assert.Equal(25, query.PageSize);
    }

    [Theory]
    [InlineData("?page=0", "page")]
    [InlineData("?pageSize=0", "pageSize")]
    [InlineData("?pageSize=101", "pageSize")]
    [InlineData("?sourceControlEnvironmentKey=unknown", "sourceControlEnvironmentKey")]
    [InlineData("?sourceControlEnvironmentKey=%20", "sourceControlEnvironmentKey")]
    public async Task List_RejectsInvalidInput(string query, string field)
    {
        using var response = await _client.GetAsync($"/api/webui/debug/pull-requests{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _));
        Assert.Empty(_factory.ListHandler.Queries);
    }

    [Fact]
    public async Task Detail_UsesUrlSafeCompositeIdentityAndLoadsFeedbackOnlyOnDemand()
    {
        using var list = await _client.GetAsync("/api/webui/debug/pull-requests");
        Assert.Empty(_factory.DetailHandler.Queries);

        using var response = await _client.GetAsync(
            "/api/webui/debug/pull-requests/ADO%20PRIMARY/team%2Forders/PR%20%2342");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var query = Assert.Single(_factory.DetailHandler.Queries);
        Assert.Equal("ado primary", query.SourceControlEnvironmentKey);
        Assert.Equal("team/orders", query.RepositoryKey);
        Assert.Equal("PR #42", query.PullRequestId);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("present", payload.GetProperty("feedbackTrace").GetProperty("markerStatus").GetString());
        Assert.False(payload.TryGetProperty("comments", out _));
        var json = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("comment body", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("credential", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Detail_ReturnsNotFoundAndValidatesUnknownEnvironment()
    {
        using var missing = await _client.GetAsync(
            "/api/webui/debug/pull-requests/ado%20primary/team%2Forders/missing");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        using var invalid = await _client.GetAsync(
            "/api/webui/debug/pull-requests/unknown/team%2Forders/42");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Single(_factory.DetailHandler.Queries);
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private sealed class PullRequestDebugApiFactory : SilentWebApplicationFactory
    {
        private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"agent-controller-pr-debug-{Guid.NewGuid():N}.db");
        public RecordingListHandler ListHandler { get; } = new();
        public RecordingDetailHandler DetailHandler { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["agentController:workerEnabled"] = "false",
                    ["persistence:connectionString"] = $"Data Source={_databasePath}",
                    ["workSource:provider"] = "LocalFake",
                    ["feedback:enabled"] = "false",
                    ["feedback:provider"] = "None",
                }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IRepositoryStore>();
                services.RemoveAll<IConnectionStore>();
                services.RemoveAll<IQueryHandler<ListPullRequestDiagnosticsQuery, PullRequestDiagnosticsPage>>();
                services.RemoveAll<IQueryHandler<GetPullRequestDiagnosticsQuery, PullRequestDiagnosticDetail?>>();
                services.AddSingleton<IRepositoryStore>(new RepositoryStore());
                services.AddSingleton<IConnectionStore>(new ConnectionStore());
                services.AddSingleton<IQueryHandler<ListPullRequestDiagnosticsQuery, PullRequestDiagnosticsPage>>(ListHandler);
                services.AddSingleton<IQueryHandler<GetPullRequestDiagnosticsQuery, PullRequestDiagnosticDetail?>>(DetailHandler);
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && File.Exists(_databasePath)) File.Delete(_databasePath);
        }
    }

    public sealed class RecordingListHandler : IQueryHandler<ListPullRequestDiagnosticsQuery, PullRequestDiagnosticsPage>
    {
        public List<ListPullRequestDiagnosticsQuery> Queries { get; } = [];
        public Task<PullRequestDiagnosticsPage> ExecuteAsync(ListPullRequestDiagnosticsQuery query, CancellationToken cancellationToken)
        {
            Queries.Add(query);
            return Task.FromResult(new PullRequestDiagnosticsPage
            {
                Page = query.Page,
                PageSize = query.PageSize,
                Total = 61,
                Items =
                [
                    new PullRequestDiagnosticSummary
                    {
                        PullRequestId = "42", Title = "Fix pickup", Url = "https://example.test/pr/42",
                        SourceControlEnvironmentKey = "ado primary", RepositoryKey = "team/orders",
                        Status = "active", Request = PullRequestRequestMatch.Both, Eligible = true,
                    },
                    new PullRequestDiagnosticSummary
                    {
                        PullRequestId = "43", Title = "Needs review", Url = "https://example.test/pr/43",
                        SourceControlEnvironmentKey = "ado primary", RepositoryKey = "team/orders",
                        Status = "active", Request = PullRequestRequestMatch.None, Eligible = false,
                    },
                ],
                Failures = [new ManagedPullRequestDiscoveryFailure
                {
                    SourceControlEnvironmentKey = "ado primary", RepositoryKey = "other", Message = "Unavailable",
                }],
            });
        }
    }

    public sealed class RecordingDetailHandler : IQueryHandler<GetPullRequestDiagnosticsQuery, PullRequestDiagnosticDetail?>
    {
        public List<GetPullRequestDiagnosticsQuery> Queries { get; } = [];
        public Task<PullRequestDiagnosticDetail?> ExecuteAsync(GetPullRequestDiagnosticsQuery query, CancellationToken cancellationToken)
        {
            Queries.Add(query);
            if (query.PullRequestId == "missing") return Task.FromResult<PullRequestDiagnosticDetail?>(null);
            return Task.FromResult<PullRequestDiagnosticDetail?>(new PullRequestDiagnosticDetail
            {
                PullRequestId = query.PullRequestId, Title = "Fix pickup", Url = "https://example.test/pr/42",
                SourceControlEnvironmentKey = query.SourceControlEnvironmentKey, RepositoryKey = query.RepositoryKey,
                Status = "active", SourceBranch = "refs/heads/fix", TargetBranch = "refs/heads/main",
                Labels = ["agent-rework-requested"], Request = PullRequestRequestMatch.Revival,
                Outcome = PullRequestDiagnosticOutcome.Eligible, Eligible = true,
                FeedbackTrace = new ReviewFeedbackCheckTrace
                {
                    PullRequestId = query.PullRequestId, MarkerStatus = FeedbackMarkerCheckStatus.Present,
                    ReviewerAllowlistConfigured = true, QualifyingThreadCount = 1, IsAccepted = true,
                },
            });
        }
    }

    private sealed class RepositoryStore : IRepositoryStore
    {
        public Task<IReadOnlyList<RepositoryProfile>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RepositoryProfile>>([new() { Key = "team/orders", RepositoryHostConnectionKey = "ado primary" }]);
        public Task<RepositoryProfile?> GetByKeyAsync(string key, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> CreateAsync(RepositoryProfile profile, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> UpdateAsync(RepositoryProfile profile, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task UpsertAsync(RepositoryProfile profile, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class ConnectionStore : IConnectionStore
    {
        public Task<IReadOnlyList<ConnectionProfile>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ConnectionProfile>>([new()
            {
                Key = "ado primary", DisplayName = "Azure Repos", Capabilities = [ConnectionCapability.Repositories],
                ProviderSettings = new AzureDevOpsConnectionSettings
                {
                    OrganizationUrl = "https://dev.azure.com/example",
                    PersonalAccessTokenReference = new AgentController.Domain.Secrets.SecretReference { Name = "very-secret" },
                },
            }]);
        public Task<ConnectionProfile?> GetByKeyAsync(string key, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> CreateAsync(ConnectionProfile profile, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> UpdateAsync(ConnectionProfile profile, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}

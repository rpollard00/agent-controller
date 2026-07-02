using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentController.Api.Models;
using AgentController.Application;
using AgentController.Domain;
using AgentController.Infrastructure.Data;
using AgentController.Infrastructure.Options;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgentController.Api.Tests;

/// <summary>
/// Integration tests for the POST /runs/{runId}/events endpoint.
/// Covers validation, idempotency, route/body consistency, and
/// expected error responses.
///
/// Uses an in-memory WebApplicationFactory with a real HTTP client
/// against the full middleware pipeline.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "IAsyncLifetime.DisposeAsync disposes all owned fields.")]
public class RuntimeEventApiTests : IAsyncLifetime
{
    private SilentWebApplicationFactory _factory = null!;
    private HttpClient _client = null!;
    private string _dbPath = null!;
    private string _runId = null!;

    public async Task InitializeAsync()
    {
        // Use SQLite in-memory database for tests. Environment variables
        // override appsettings.json values reliably with WebApplicationFactory.
        var dbPath = $"/tmp/agent-controller-test-{Guid.NewGuid():N}.db";
        _dbPath = dbPath;
        // Ensure the cache directory exists for the database file
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        Environment.SetEnvironmentVariable("persistence__connectionString", $"Data Source={dbPath}");
        Environment.SetEnvironmentVariable("persistence__provider", "Sqlite");
        Environment.SetEnvironmentVariable("agentController__workerId", "test-api-worker");
        Environment.SetEnvironmentVariable("agentController__workerEnabled", "false");

        _factory = new SilentWebApplicationFactory();

        // Create database schema before any HTTP requests
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentControllerDbContext>();
        await db.Database.OpenConnectionAsync();
        await db.Database.EnsureCreatedAsync();

        _client = _factory.CreateClient();

        // Create a work item and advance a run to AwaitingResult
        await SeedRunToAwaitingResult();
    }

    public Task DisposeAsync()
    {
        _client?.Dispose();
        _factory?.Dispose();

        // Clean up environment variables set by this test
        Environment.SetEnvironmentVariable("persistence__connectionString", null);
        Environment.SetEnvironmentVariable("persistence__provider", null);
        Environment.SetEnvironmentVariable("agentController__workerId", null);
        Environment.SetEnvironmentVariable("agentController__workerEnabled", null);

        // Clean up the temp database file if it exists
        if (_dbPath is not null && File.Exists(_dbPath))
        {
            try { File.Delete(_dbPath); } catch { }
        }

        return Task.CompletedTask;
    }

    private async Task SeedRunToAwaitingResult()
    {
        // Create work item
        var wiRequest = new CreateWorkItemRequest
        {
            RepoKey = "test-repo",
            Title = "Test Work Item",
            Tags = ["agent-ready"],
            Status = "New",
        };

        var wiResponse = await _client.PostAsJsonAsync("/work-items", wiRequest);
        Assert.Equal(HttpStatusCode.Created, wiResponse.StatusCode);

        var workItem = await wiResponse.Content.ReadFromJsonAsync<WorkCandidate>();
        Assert.NotNull(workItem);

        // Manually advance the run through the lifecycle by calling the internal
        // lifecycle service. We go through the service provider.
        using var scope = _factory.Services.CreateScope();
        var lifecycle = scope.ServiceProvider.GetRequiredService<IRunLifecycleService>();
        var runStore = scope.ServiceProvider.GetRequiredService<IAgentRunStore>();
        var workItemStore = scope.ServiceProvider.GetRequiredService<IWorkItemStore>();

        // Claim the work item
        await workItemStore.TryClaimAsync(workItem!.Id, new ClaimRequest { WorkerId = "test-api" }, CancellationToken.None);

        // Create run
        var run = await lifecycle.CreateRunForWorkItemAsync(workItem.Id, "test-api", CancellationToken.None);
        _runId = run.RunId;

        // Advance through controller-owned states
        await lifecycle.TransitionAsync(_runId, RunLifecycleState.EnvironmentProvisioning, CancellationToken.None);
        await lifecycle.TransitionAsync(_runId, RunLifecycleState.EnvironmentReady, CancellationToken.None);
        await lifecycle.TransitionAsync(_runId, RunLifecycleState.RepositoryCloning, CancellationToken.None);
        await lifecycle.TransitionAsync(_runId, RunLifecycleState.RepositoryReady, CancellationToken.None);
        await lifecycle.TransitionAsync(_runId, RunLifecycleState.ContextInjected, CancellationToken.None);
        await lifecycle.TransitionAsync(_runId, RunLifecycleState.AgentStarting, CancellationToken.None);
        await lifecycle.TransitionAsync(_runId, RunLifecycleState.AgentRunning, CancellationToken.None);
        await lifecycle.TransitionAsync(_runId, RunLifecycleState.AwaitingResult, CancellationToken.None);
    }

    // ── Successful event ingestion ─────────────────────────────────

    [Fact]
    public async Task PostEvent_Heartbeat_ReturnsOk()
    {
        var request = new
        {
            eventId = $"evt_hb_{Guid.NewGuid():N}",
            eventType = RuntimeEventTypes.Heartbeat,
        };

        var response = await _client.PostAsJsonAsync($"/runs/{_runId}/events", request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(_runId, body.GetProperty("runId").GetString());
    }

    [Fact]
    public async Task PostEvent_Status_ReturnsOk()
    {
        var request = new
        {
            eventId = $"evt_status_{Guid.NewGuid():N}",
            eventType = RuntimeEventTypes.Status,
            message = "Running unit tests",
        };

        var response = await _client.PostAsJsonAsync($"/runs/{_runId}/events", request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task PostEvent_CompletedWithProPullRequestOpened_ReturnsOk()
    {
        var request = new
        {
            eventId = $"evt_comp_{Guid.NewGuid():N}",
            eventType = RuntimeEventTypes.Completed,
            message = "Done",
            payload = new Dictionary<string, object>
            {
                ["outcome"] = CompletionOutcomes.PullRequestOpened,
                ["pullRequestUrl"] = "https://dev.azure.com/pr/42",
                ["branchName"] = "agent/42-fix",
            },
        };

        var response = await _client.PostAsJsonAsync($"/runs/{_runId}/events", request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ── Validation: missing required fields ────────────────────────

    [Fact]
    public async Task PostEvent_MissingEventId_Returns422()
    {
        var request = new
        {
            eventId = "",
            eventType = RuntimeEventTypes.Status,
        };

        var response = await _client.PostAsJsonAsync($"/runs/{_runId}/events", request);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("eventId", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task PostEvent_MissingEventType_Returns422()
    {
        var request = new
        {
            eventId = $"evt_{Guid.NewGuid():N}",
            eventType = "",
        };

        var response = await _client.PostAsJsonAsync($"/runs/{_runId}/events", request);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("eventType", body.GetProperty("error").GetString());
    }

    // ── Validation: runId mismatch ─────────────────────────────────

    [Fact]
    public async Task PostEvent_RunIdMismatch_Returns422()
    {
        var request = new RuntimeEventRequest
        {
            RunId = "run_different",
            EventId = $"evt_{Guid.NewGuid():N}",
            EventType = RuntimeEventTypes.Status,
        };

        var response = await _client.PostAsJsonAsync($"/runs/{_runId}/events", request);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var error = body.GetProperty("error").GetString();
        Assert.Contains("mismatch", error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(_runId, body.GetProperty("runId").GetString());
    }

    [Fact]
    public async Task PostEvent_RunIdMatches_AcceptsEvent()
    {
        var request = new RuntimeEventRequest
        {
            RunId = _runId,
            EventId = $"evt_match_{Guid.NewGuid():N}",
            EventType = RuntimeEventTypes.Status,
            Message = "RunId matches route",
        };

        var response = await _client.PostAsJsonAsync($"/runs/{_runId}/events", request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ── Validation: unsupported severity ──────────────────────────

    [Fact]
    public async Task PostEvent_OutOfRangeSeverity_Returns422()
    {
        var request = new RuntimeEventRequest
        {
            EventId = $"evt_{Guid.NewGuid():N}",
            EventType = RuntimeEventTypes.Status,
            Severity = (EventSeverity)99,
        };

        var response = await _client.PostAsJsonAsync($"/runs/{_runId}/events", request);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("severity", body.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    // ── Runtime events arrive with severity as a lowercase string ────
    // pi-materia's agent-controller webhook preset sends severity as a lowercase
    // string ("info"/"warning"/"error"/"critical") per docs/arch.md §10.2. The
    // minimal-API JSON binder must accept these (regression guard for the
    // JsonStringEnumConverter wired in Program.cs).
    [Theory]
    [InlineData("info")]
    [InlineData("warning")]
    [InlineData("error")]
    [InlineData("critical")]
    public async Task PostEvent_StringSeverity_Accepted(string severity)
    {
        var json = $"{{\"eventId\":\"evt_{Guid.NewGuid():N}\",\"eventType\":\"{RuntimeEventTypes.Status}\",\"severity\":\"{severity}\",\"message\":\"ok\"}}";
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

        var response = await _client.PostAsync($"/runs/{_runId}/events", content);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ── Validation: far-future occurredAt ──────────────────────────

    [Fact]
    public async Task PostEvent_FarFutureOccurredAt_Returns422()
    {
        var request = new RuntimeEventRequest
        {
            EventId = $"evt_{Guid.NewGuid():N}",
            EventType = RuntimeEventTypes.Status,
            OccurredAt = DateTimeOffset.UtcNow.AddHours(1),
        };

        var response = await _client.PostAsJsonAsync($"/runs/{_runId}/events", request);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("too far in the future", body.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    // ── Idempotency (duplicate eventId) ────────────────────────────

    [Fact]
    public async Task PostEvent_DuplicateEventId_Returns409()
    {
        var eventId = $"evt_dup_{Guid.NewGuid():N}";
        var request = new
        {
            eventId,
            eventType = RuntimeEventTypes.Status,
            message = "First",
        };

        // First ingest
        var first = await _client.PostAsJsonAsync($"/runs/{_runId}/events", request);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        // Duplicate
        var second = await _client.PostAsJsonAsync($"/runs/{_runId}/events", request);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        var body = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("already been processed", body.GetProperty("error").GetString());
        Assert.Equal(_runId, body.GetProperty("runId").GetString());
        Assert.Equal(eventId, body.GetProperty("eventId").GetString());
    }

    // ── Terminal state rejection ───────────────────────────────────

    [Fact]
    public async Task PostEvent_OnTerminalRun_Returns422()
    {
        // First complete the run
        var compRequest = new
        {
            eventId = $"evt_term_comp_{Guid.NewGuid():N}",
            eventType = RuntimeEventTypes.Completed,
            payload = new Dictionary<string, object> { ["outcome"] = CompletionOutcomes.Failed },
        };

        await _client.PostAsJsonAsync($"/runs/{_runId}/events", compRequest);

        // Now try to send another event to the terminal run
        var lateRequest = new
        {
            eventId = $"evt_term_late_{Guid.NewGuid():N}",
            eventType = RuntimeEventTypes.Status,
            message = "Too late",
        };

        var response = await _client.PostAsJsonAsync($"/runs/{_runId}/events", lateRequest);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (HttpStatusCode)422);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("terminal state", body.GetProperty("error").GetString());
    }

    // ── Unsupported event type ─────────────────────────────────────

    [Fact]
    public async Task PostEvent_UnsupportedType_Returns422()
    {
        var request = new
        {
            eventId = $"evt_bad_{Guid.NewGuid():N}",
            eventType = "runtime.bogus_event",
        };

        var response = await _client.PostAsJsonAsync($"/runs/{_runId}/events", request);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (HttpStatusCode)422);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("Unsupported runtime event type", body.GetProperty("error").GetString());
    }

    // ── Unsupported completion outcome ─────────────────────────────

    [Fact]
    public async Task PostEvent_UnsupportedCompletionOutcome_Returns422()
    {
        var request = new
        {
            eventId = $"evt_bad_outcome_{Guid.NewGuid():N}",
            eventType = RuntimeEventTypes.Completed,
            payload = new Dictionary<string, object> { ["outcome"] = "magical_solution" },
        };

        var response = await _client.PostAsJsonAsync($"/runs/{_runId}/events", request);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (HttpStatusCode)422);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("Unsupported completion outcome", body.GetProperty("error").GetString());
    }

    // ── Nonexistent run ────────────────────────────────────────────

    [Fact]
    public async Task PostEvent_NonexistentRun_Returns422()
    {
        var request = new
        {
            eventId = $"evt_{Guid.NewGuid():N}",
            eventType = RuntimeEventTypes.Status,
        };

        var response = await _client.PostAsJsonAsync("/runs/run_nonexistent/events", request);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (HttpStatusCode)422);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("not found", body.GetProperty("error").GetString());
    }

    // ── Accepted tolerance on progressed runs ─────────────────────

    [Fact]
    public async Task PostEvent_AcceptedOnAwaitingResult_ToleratedReturns200()
    {
        // The seeded run is already in AwaitingResult (the benign race where the
        // PollingWorker advances past AgentRunning before the runtime boots and
        // POSTs accepted). The controller tolerates this: 200 OK, state unchanged,
        // runtime id + heartbeat recorded. (This was previously a 422.)
        var eventId = $"evt_acc_{Guid.NewGuid():N}";
        var request = new
        {
            eventId,
            eventType = RuntimeEventTypes.Accepted,
            runtimeRunId = "pi_late_boot",
            message = "Accepted late",
        };

        var response = await _client.PostAsJsonAsync($"/runs/{_runId}/events", request);
        Assert.Equal(HttpStatusCode.OK, (HttpStatusCode)200);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(_runId, body.GetProperty("runId").GetString());
        Assert.Equal("AwaitingResult", body.GetProperty("status").GetString());
        Assert.Equal("pi_late_boot", body.GetProperty("runtimeRunId").GetString());
    }

    // ── No runId in body is okay (route is authoritative) ──────────

    [Fact]
    public async Task PostEvent_NoBodyRunId_AcceptsEvent()
    {
        // Body without runId — route runId is used as fallback
        var request = new
        {
            eventId = $"evt_norunid_{Guid.NewGuid():N}",
            eventType = RuntimeEventTypes.Heartbeat,
        };

        var response = await _client.PostAsJsonAsync($"/runs/{_runId}/events", request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}

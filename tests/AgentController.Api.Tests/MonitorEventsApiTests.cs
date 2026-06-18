using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AgentController.Api.Models;
using AgentController.Application;
using AgentController.Domain;
using AgentController.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AgentController.Api.Tests;

/// <summary>
/// Integration tests for the monitoring/local sync channel
/// (<c>GET /api/monitor/events</c>).
///
/// Covers the JSON snapshot (backward-compatible summary + additive runtime
/// events), cap behavior, validation, and the server-sent-event stream that
/// pushes updates as events are appended. Runtime events are fed by writing the
/// canonical <c>{runRoot}/{runId}/events/events.jsonl</c> artifact that the
/// monitor reads.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "IAsyncLifetime.DisposeAsync disposes all owned fields.")]
[Collection("ApiWebFactory")]
public class MonitorEventsApiTests : IAsyncLifetime
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dbPath = null!;
    private string _runRoot = null!;
    private string _runId = null!;

    public async Task InitializeAsync()
    {
        _dbPath = $"/tmp/agent-controller-monitor-{Guid.NewGuid():N}.db";
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
        _runRoot = Path.Combine(Path.GetTempPath(), "monitor-runs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_runRoot);

        // Env vars override appsettings reliably with WebApplicationFactory and
        // must be set before the host is built so options/validate-on-start sees them.
        Environment.SetEnvironmentVariable("persistence__connectionString", $"Data Source={_dbPath}");
        Environment.SetEnvironmentVariable("persistence__provider", "Sqlite");
        Environment.SetEnvironmentVariable("agentController__workerId", "test-monitor-worker");
        Environment.SetEnvironmentVariable("agentController__workerEnabled", "false");
        Environment.SetEnvironmentVariable("agentController__runRoot", _runRoot);

        _factory = new WebApplicationFactory<Program>();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentControllerDbContext>();
        await db.Database.OpenConnectionAsync();
        await db.Database.EnsureCreatedAsync();

        // Resolve the monitor from the container to prove the DI wiring works.
        var monitor = scope.ServiceProvider.GetService<IRuntimeEventMonitor>();
        Assert.NotNull(monitor);

        _client = _factory.CreateClient();
        _runId = await SeedRunAsync();
    }

    public Task DisposeAsync()
    {
        _client?.Dispose();
        _factory?.Dispose();

        Environment.SetEnvironmentVariable("persistence__connectionString", null);
        Environment.SetEnvironmentVariable("persistence__provider", null);
        Environment.SetEnvironmentVariable("agentController__workerId", null);
        Environment.SetEnvironmentVariable("agentController__workerEnabled", null);
        Environment.SetEnvironmentVariable("agentController__runRoot", null);

        if (_dbPath is not null && File.Exists(_dbPath))
        {
            try { File.Delete(_dbPath); } catch { }
        }

        if (_runRoot is not null && Directory.Exists(_runRoot))
        {
            try { Directory.Delete(_runRoot, recursive: true); } catch { }
        }

        return Task.CompletedTask;
    }

    private async Task<string> SeedRunAsync()
    {
        var wiRequest = new CreateWorkItemRequest
        {
            RepoKey = "test-repo",
            Title = "Monitor Test Work Item",
            Tags = ["agent-ready"],
            Status = "New",
        };

        var wiResponse = await _client.PostAsJsonAsync("/work-items", wiRequest);
        Assert.Equal(HttpStatusCode.Created, wiResponse.StatusCode);

        var workItem = await wiResponse.Content.ReadFromJsonAsync<WorkCandidate>();
        Assert.NotNull(workItem);

        using var scope = _factory.Services.CreateScope();
        var lifecycle = scope.ServiceProvider.GetRequiredService<IRunLifecycleService>();
        var workItemStore = scope.ServiceProvider.GetRequiredService<IWorkItemStore>();

        await workItemStore.TryClaimAsync(
            workItem!.Id,
            new ClaimRequest { WorkerId = "test-monitor" },
            CancellationToken.None);

        var run = await lifecycle.CreateRunForWorkItemAsync(
            workItem.Id,
            "test-monitor",
            CancellationToken.None);

        return run.RunId;
    }

    private string EventsFile => Path.Combine(_runRoot, _runId, "events", "events.jsonl");

    private void WriteEventsFile(params string[] lines)
    {
        var eventsDir = Path.Combine(_runRoot, _runId, "events");
        Directory.CreateDirectory(eventsDir);
        File.WriteAllText(EventsFile, lines.Length == 0 ? "" : string.Join("\n", lines) + "\n");
    }

    private void AppendEventLine(string line)
    {
        var eventsDir = Path.Combine(_runRoot, _runId, "events");
        Directory.CreateDirectory(eventsDir);
        File.AppendAllText(EventsFile, line + "\n");
    }

    /// <summary>Build a single runtime event JSONL line with deterministic fields.</summary>
    private string EventLine(string eventId, string eventType, int sequence, string severity = "info") =>
        "{\"eventId\":\"" + eventId + "\","
        + "\"runId\":\"" + _runId + "\","
        + "\"eventType\":\"" + eventType + "\","
        + "\"sequence\":" + sequence.ToString(System.Globalization.CultureInfo.InvariantCulture) + ","
        + "\"occurredAt\":\"2026-06-17T12:00:0" + (sequence % 10).ToString(System.Globalization.CultureInfo.InvariantCulture) + "Z\","
        + "\"severity\":\"" + severity + "\","
        + "\"message\":\"msg-" + sequence.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\"}";

    // ── DI wiring ────────────────────────────────────────────────────

    [Fact]
    public void RuntimeEventMonitor_IsRegistered_AsSingleton()
    {
        // The monitoring endpoint depends on this; prove the DI extension wired it.
        using var scope = _factory.Services.CreateScope();
        var monitor = scope.ServiceProvider.GetRequiredService<IRuntimeEventMonitor>();
        Assert.NotNull(monitor);
    }

    // ── JSON snapshot ────────────────────────────────────────────────

    [Fact]
    public async Task GetMonitorEvents_RunExistsWithArtifact_ReturnsSnapshotWithRuntimeEvents()
    {
        WriteEventsFile(
            EventLine("evt_1", RuntimeEventTypes.Accepted, 1),
            EventLine("evt_2", RuntimeEventTypes.Status, 2, "warning"));

        var response = await _client.GetAsync($"/api/monitor/events?runId={_runId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
        var root = doc.RootElement;

        // Existing/backward-compatible monitoring summary fields.
        Assert.Equal(_runId, root.GetProperty("runId").GetString());
        Assert.True(root.TryGetProperty("status", out _));
        Assert.True(root.TryGetProperty("generatedAt", out _));

        // Additive runtimeEvents payload.
        var events = root.GetProperty("runtimeEvents");
        Assert.Equal(_runId, events.GetProperty("runId").GetString());
        Assert.Equal(2, events.GetProperty("events").GetArrayLength());
        Assert.Equal(200, events.GetProperty("cap").GetInt32());
        Assert.False(events.GetProperty("truncated").GetBoolean());
        Assert.Equal(2, events.GetProperty("totalAvailable").GetInt32());

        // Events carry parsed fields the UI formats on.
        var first = events.GetProperty("events")[0];
        Assert.Equal(RuntimeEventTypes.Accepted, first.GetProperty("eventType").GetString());
        Assert.Equal("evt_1", first.GetProperty("eventId").GetString());
        Assert.Equal("Valid", first.GetProperty("status").GetString());
    }

    [Fact]
    public async Task GetMonitorEvents_NoArtifact_ReturnsEmptyRuntimeEvents()
    {
        // No events.jsonl written for this run.
        var response = await _client.GetAsync($"/api/monitor/events?runId={_runId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());

        var events = doc.RootElement.GetProperty("runtimeEvents");
        Assert.Equal(0, events.GetProperty("events").GetArrayLength());
        Assert.Equal(200, events.GetProperty("cap").GetInt32());
        Assert.False(events.GetProperty("truncated").GetBoolean());
        Assert.Equal(0, events.GetProperty("totalAvailable").GetInt32());
    }

    [Fact]
    public async Task GetMonitorEvents_MissingRunId_ReturnsBadRequest()
    {
        var response = await _client.GetAsync("/api/monitor/events");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetMonitorEvents_UnknownRun_ReturnsNotFound()
    {
        var response = await _client.GetAsync("/api/monitor/events?runId=does-not-exist");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetMonitorEvents_CapQueryParameter_LimitsToNewest()
    {
        WriteEventsFile(
            EventLine("evt_1", RuntimeEventTypes.Accepted, 1),
            EventLine("evt_2", RuntimeEventTypes.Status, 2),
            EventLine("evt_3", RuntimeEventTypes.Heartbeat, 3),
            EventLine("evt_4", RuntimeEventTypes.Status, 4),
            EventLine("evt_5", RuntimeEventTypes.Completed, 5));

        var response = await _client.GetAsync($"/api/monitor/events?runId={_runId}&cap=2");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());

        var events = doc.RootElement.GetProperty("runtimeEvents");
        Assert.Equal(2, events.GetProperty("events").GetArrayLength());
        Assert.Equal(2, events.GetProperty("cap").GetInt32());
        Assert.True(events.GetProperty("truncated").GetBoolean());
        Assert.Equal(5, events.GetProperty("totalAvailable").GetInt32());

        // Newest two only (oldest-first within the kept window).
        Assert.Equal("evt_4", events.GetProperty("events")[0].GetProperty("eventId").GetString());
        Assert.Equal("evt_5", events.GetProperty("events")[1].GetProperty("eventId").GetString());
    }

    [Fact]
    public async Task GetMonitorEvents_MalformedLine_StaysVisibleAsMalformed()
    {
        WriteEventsFile(
            EventLine("evt_1", RuntimeEventTypes.Accepted, 1),
            "{not valid json");

        var response = await _client.GetAsync($"/api/monitor/events?runId={_runId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());

        var events = doc.RootElement.GetProperty("runtimeEvents").GetProperty("events");
        Assert.Equal(2, events.GetArrayLength());
        Assert.Equal("Malformed", events[1].GetProperty("status").GetString());
        Assert.True(events[1].TryGetProperty("rawLine", out _));
        Assert.True(events[1].TryGetProperty("parseError", out _));
    }

    // ── SSE stream ───────────────────────────────────────────────────

    [Fact]
    public async Task GetMonitorEvents_AcceptEventStream_EmitsUpdatesAsAppended()
    {
        WriteEventsFile(EventLine("evt_1", RuntimeEventTypes.Accepted, 1));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/monitor/events?runId={_runId}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var response = await _client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cts.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            "text/event-stream",
            response.Content.Headers.ContentType?.MediaType,
            ignoreCase: true);

        await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(stream);

        // Initial snapshot should arrive promptly (first emission is immediate).
        var first = await ReadMonitorEventAsync(reader, cts.Token);
        Assert.Equal(_runId, first.GetProperty("runId").GetString());
        Assert.Equal(1, first.GetProperty("runtimeEvents").GetProperty("events").GetArrayLength());

        // Append a new event; the stream must push an updated payload.
        AppendEventLine(EventLine("evt_2", RuntimeEventTypes.Status, 2, "warning"));

        var second = await ReadMonitorEventAsync(reader, cts.Token);
        Assert.Equal(2, second.GetProperty("runtimeEvents").GetProperty("events").GetArrayLength());
        Assert.Equal(
            "evt_2",
            second.GetProperty("runtimeEvents").GetProperty("events")[1]
                .GetProperty("eventId").GetString());
    }

    [Fact]
    public async Task GetMonitorEvents_StreamFlag_AlsoActivatesSse()
    {
        WriteEventsFile(EventLine("evt_1", RuntimeEventTypes.Heartbeat, 1));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        // No Accept header — rely on ?stream=true alone.
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/monitor/events?runId={_runId}&stream=true");

        using var response = await _client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cts.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            "text/event-stream",
            response.Content.Headers.ContentType?.MediaType,
            ignoreCase: true);

        await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(stream);

        var payload = await ReadMonitorEventAsync(reader, cts.Token);
        Assert.Equal(_runId, payload.GetProperty("runId").GetString());
    }

    /// <summary>
    /// Read one SSE event from the stream, asserting it is a <c>monitor</c> event,
    /// and return the parsed <c>data:</c> JSON payload.
    /// </summary>
    private static async Task<JsonElement> ReadMonitorEventAsync(StreamReader reader, CancellationToken ct)
    {
        string? eventType = null;
        var data = new StringBuilder();

        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            if (line.Length == 0)
            {
                // Blank line terminates the event.
                if (eventType is not null && data.Length > 0)
                {
                    using var doc = JsonDocument.Parse(data.ToString());
                    return doc.RootElement.Clone();
                }

                eventType = null;
                data.Clear();
                continue;
            }

            if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                eventType = line["event:".Length..].TrimStart();
                Assert.Equal("monitor", eventType);
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                var d = line["data:".Length..];
                if (d.Length > 0 && d[0] == ' ')
                {
                    d = d[1..];
                }

                data.Append(d);
            }
        }

        throw new TimeoutException("No SSE monitor event received before the stream closed.");
    }
}

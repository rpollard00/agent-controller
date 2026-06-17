using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentController.Api.Models;
using AgentController.Domain;

namespace AgentController.Api;

/// <summary>
/// Helpers for the <c>/api/monitor/events</c> monitoring/local sync channel.
///
/// Builds the additive monitoring payload (run summary + runtime event snapshot)
/// and drives the server-sent-event stream that pushes updates as events are
/// appended. Kept separate from <c>Program.cs</c> so the SSE change-detection
/// loop stays readable and unit-testable, and so <c>Program.cs</c> only wires
/// the endpoint.
/// </summary>
internal static class MonitoringEndpoint
{
    /// <summary>SSE event name used for monitoring updates.</summary>
    public const string SseEventType = "monitor";

    /// <summary>
    /// Polling interval for the SSE stream. Bounds how quickly clients observe
    /// appended events while keeping artifact/file reads infrequent. The first
    /// emission is always immediate so clients receive an initial snapshot
    /// without waiting for the first tick.
    /// </summary>
    public static readonly TimeSpan SsePollInterval = TimeSpan.FromSeconds(2);

    // Web defaults (camelCase) match Results.Ok serialization, and the string enum
    // converter makes status/severity readable on the wire for UI consumers while
    // staying scoped to this channel (other endpoints are unaffected). The same
    // options are used by the snapshot and SSE paths so their payloads match.
    private static readonly JsonSerializerOptions JsonOptions = BuildJsonOptions();

    private static JsonSerializerOptions BuildJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    /// <summary>
    /// Serialize a monitoring payload using the channel's JSON options so the JSON
    /// snapshot and the SSE data payloads are byte-identical for the same data.
    /// </summary>
    public static string Serialize(MonitorEventsResponse payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    /// <summary>
    /// True when the client requested a server-sent-event stream, either via the
    /// <c>?stream=true</c> flag or by advertising <c>text/event-stream</c> in the
    /// <c>Accept</c> header. Falls back to a JSON snapshot otherwise.
    /// </summary>
    public static bool ClientWantsStream(string? streamFlag, HttpContext httpContext)
    {
        if (string.Equals(streamFlag, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(streamFlag, "1", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return httpContext.Request.Headers.Accept.ToString()
            .Contains("text/event-stream", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Build the monitoring payload from the current run summary and runtime
    /// event snapshot.
    /// </summary>
    public static MonitorEventsResponse BuildResponse(
        AgentRunHandle run,
        MonitoringRuntimeEventSnapshot runtimeEvents)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(runtimeEvents);

        return new MonitorEventsResponse
        {
            RunId = run.RunId,
            Status = run.Status.ToString(),
            StartedAt = run.StartedAt,
            FinishedAt = run.FinishedAt,
            LastHeartbeatAt = run.LastHeartbeatAt,
            RuntimeEvents = runtimeEvents,
        };
    }

    /// <summary>
    /// Stream monitoring updates to the client as server-sent events.
    ///
    /// Polls <paramref name="poll"/> at <see cref="SsePollInterval"/> and emits a
    /// <see cref="SseEventType"/> event whenever the run summary or runtime event
    /// stream changes. The first emission is immediate so clients receive an
    /// initial snapshot without waiting. The loop ends when the client disconnects
    /// (<paramref name="clientDisconnectToken"/>) or the stream is cancelled.
    /// Transient non-cancellation failures from <paramref name="poll"/> propagate
    /// and end the stream so clients can reconnect cleanly.
    /// </summary>
    /// <param name="response">The HTTP response to write the SSE stream to.</param>
    /// <param name="poll">
    /// Produces the current monitoring payload. Called once per tick; receives the
    /// disconnect token so producers can cooperate with cancellation.
    /// </param>
    /// <param name="clientDisconnectToken">
    /// Token that cancels when the client disconnects (e.g.
    /// <see cref="HttpContext.RequestAborted"/>).
    /// </param>
    public static async Task StreamAsync(
        HttpResponse response,
        Func<CancellationToken, Task<MonitorEventsResponse>> poll,
        CancellationToken clientDisconnectToken)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(poll);

        response.Headers.CacheControl = "no-cache";
        response.Headers["X-Accel-Buffering"] = "no";
        await response.StartAsync(clientDisconnectToken);

        string? lastSignature = null;

        while (!clientDisconnectToken.IsCancellationRequested)
        {
            MonitorEventsResponse payload;
            try
            {
                payload = await poll(clientDisconnectToken);
            }
            catch (OperationCanceledException) when (clientDisconnectToken.IsCancellationRequested)
            {
                break;
            }

            var signature = ComputeSignature(payload);
            if (!string.Equals(signature, lastSignature, StringComparison.Ordinal))
            {
                lastSignature = signature;
                await WriteEventAsync(response, payload, clientDisconnectToken);
            }

            try
            {
                await Task.Delay(SsePollInterval, clientDisconnectToken);
            }
            catch (OperationCanceledException) when (clientDisconnectToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    /// <summary>
    /// A stable signature that changes whenever the run summary or the runtime
    /// event stream content meaningfully changes, so the SSE loop only emits on
    /// real updates. Captures lifecycle status/heartbeats plus the event stream
    /// head (total/count/truncation) and tail (newest entry identity).
    /// </summary>
    private static string ComputeSignature(MonitorEventsResponse payload)
    {
        var events = payload.RuntimeEvents;
        var newest = events.Events.Count > 0
            ? events.Events[^1]
            : null;

        return string.Concat(
            payload.Status, "|",
            payload.FinishedAt?.UtcTicks.ToString(CultureInfo.InvariantCulture), "|",
            payload.LastHeartbeatAt?.UtcTicks.ToString(CultureInfo.InvariantCulture), "|",
            events.TotalAvailable?.ToString(CultureInfo.InvariantCulture), "/", events.Events.Count.ToString(CultureInfo.InvariantCulture), "/", events.Truncated, "|",
            newest?.Index.ToString(CultureInfo.InvariantCulture), "/", newest?.EventId, "/", newest?.OccurredAt?.UtcTicks.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Serialize one monitoring payload as an SSE event and flush it so the client
    /// receives it promptly. The JSON is emitted on a single <c>data:</c> line; the
    /// event is terminated by a blank line per the SSE spec.
    /// </summary>
    private static async Task WriteEventAsync(
        HttpResponse response,
        MonitorEventsResponse payload,
        CancellationToken cancellationToken)
    {
        var json = Serialize(payload);
        var builder = new StringBuilder(json.Length + 32)
            .Append("event: ").Append(SseEventType).Append('\n');

        // Defensive multi-line handling: web-default JSON is single-line, but split
        // so any embedded newline stays SSE-spec compliant (each line prefixed).
        foreach (var line in json.Split('\n'))
        {
            builder.Append("data: ").Append(line).Append('\n');
        }

        builder.Append('\n');

        var bytes = Encoding.UTF8.GetBytes(builder.ToString());
        await response.Body.WriteAsync(bytes.AsMemory(0, bytes.Length), cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
    }
}

/// <summary>
/// An <see cref="IResult"/> that serves the monitoring feed as a server-sent-event
/// stream. Sets the SSE content type and delegates to
/// <see cref="MonitoringEndpoint.StreamAsync"/>. Returned by the
/// <c>/api/monitor/events</c> endpoint when the client requests a stream
/// (<c>?stream=true</c> or <c>Accept: text/event-stream</c>).
/// </summary>
internal sealed class MonitoringSseResult : IResult
{
    private readonly Func<CancellationToken, Task<MonitorEventsResponse>> _poll;

    public MonitoringSseResult(Func<CancellationToken, Task<MonitorEventsResponse>> poll)
    {
        _poll = poll;
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(HttpContext httpContext)
    {
        httpContext.Response.ContentType = "text/event-stream";
        await MonitoringEndpoint.StreamAsync(
            httpContext.Response,
            _poll,
            httpContext.RequestAborted);
    }
}

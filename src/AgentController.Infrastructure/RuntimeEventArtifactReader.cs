using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentController.Domain;

namespace AgentController.Infrastructure;

/// <summary>
/// Reads runtime event JSONL artifacts from agent run directories and projects
/// them into <see cref="MonitoringRuntimeEventSnapshot"/> for the monitoring/local
/// sync feed.
///
/// The canonical artifact location is <c>{runDirectory}/events/events.jsonl</c> —
/// one JSON object per line, matching the runtime event envelope documented in
/// <c>docs/runtime-events.md</c>. No existing runtime code writes this file yet, so
/// this reader establishes the canonical path for future runtime artifact emission.
///
/// The reader is intentionally resilient: it never throws for absent files, partial
/// writes, invalid JSON lines, or large files, and it bounds memory with a
/// newest-event cap so local sync is never blocked. It is stateless and
/// dependency-free, so it is exposed as a static utility.
/// </summary>
public static class RuntimeEventArtifactReader
{
    /// <summary>
    /// Canonical relative path to the runtime event stream within a run directory.
    /// </summary>
    public const string EventStreamRelativePath = "events/events.jsonl";

    /// <summary>
    /// Newest-event cap applied when <see cref="RuntimeEventArtifactReadOptions.Cap"/>
    /// is <c>null</c>. Bounds the returned payload and the reader's working memory.
    /// </summary>
    public const int DefaultCap = 200;

    /// <summary>
    /// Default hard byte ceiling. Files at or below this size are streamed in full
    /// (oldest→newest) with a rolling-window cap. Files above it are served only from
    /// a bounded tail window so local sync never blocks on a pathologically large log.
    /// </summary>
    public const long DefaultMaxFileBytes = 128L * 1024 * 1024;

    /// <summary>
    /// Default tail-window size (in bytes) used when a file exceeds
    /// <see cref="DefaultMaxFileBytes"/>. The reader seeks to
    /// <c>length - tailBytes</c> and skips the (likely partial) first line, then
    /// parses the remaining complete lines.
    /// </summary>
    public const long DefaultTailReadBytes = 8L * 1024 * 1024;

    /// <summary>
    /// Per-line character ceiling. A line longer than this is recorded as malformed
    /// rather than parsed, protecting local sync from a single runaway payload. The
    /// preserved raw line is truncated to <see cref="MaxRawLineChars"/> for display.
    /// </summary>
    public const int MaxLineChars = 1_048_576; // 1 MiB

    /// <summary>
    /// Maximum characters of a raw line retained on a malformed entry when the source
    /// line exceeds <see cref="MaxLineChars"/>, keeping the snapshot bounded.
    /// </summary>
    public const int MaxRawLineChars = 4096;

    /// <summary>
    /// Resolve the canonical event stream path for a run directory.
    /// </summary>
    /// <param name="runDirectory">
    /// Absolute run workspace directory (<c>{runRoot}/{runId}</c>).
    /// </param>
    public static string GetEventFilePath(string runDirectory)
        => Path.Combine(runDirectory, "events", "events.jsonl");

    /// <summary>
    /// Read the runtime event stream for a run directory with default options
    /// (cap = <see cref="DefaultCap"/>, default byte limits).
    /// </summary>
    /// <param name="runDirectory">Absolute run workspace directory.</param>
    /// <param name="cancellationToken">Token to cancel the read.</param>
    public static Task<MonitoringRuntimeEventSnapshot> ReadAsync(
        string runDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runDirectory);
        return ReadAsync(
            runDirectory,
            new RuntimeEventArtifactReadOptions(),
            cancellationToken);
    }

    /// <summary>
    /// Read the runtime event stream for a run directory, applying the supplied
    /// <paramref name="options"/>.
    ///
    /// Never throws for absent files, partial writes, invalid JSON lines, or large
    /// files: absent streams return an empty snapshot; invalid lines become malformed
    /// entries that retain their raw line; large files are served from a bounded tail.
    /// Cancellation returns the partial snapshot collected so far rather than throwing.
    ///
    /// Returned <see cref="MonitoringRuntimeEventSnapshot.Events"/> are ordered
    /// oldest-first within the kept window. For the normal streamed path, each entry's
    /// <see cref="MonitoringRuntimeEvent.Index"/> is its 0-based ordinal among non-blank
    /// lines in the source stream (a stable identity across polls). For the large-file
    /// tail path, indices are relative to the tail window and
    /// <see cref="MonitoringRuntimeEventSnapshot.TotalAvailable"/> is <c>null</c>.
    /// </summary>
    /// <param name="runDirectory">Absolute run workspace directory.</param>
    /// <param name="options">Read options (runId tag, cap, byte limits).</param>
    /// <param name="cancellationToken">Token to cancel the read.</param>
    public static Task<MonitoringRuntimeEventSnapshot> ReadAsync(
        string runDirectory,
        RuntimeEventArtifactReadOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runDirectory);
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(runDirectory))
        {
            return Task.FromResult(EmptySnapshot(options.RunId, ResolveCap(options.Cap)));
        }

        return ReadInternalAsync(runDirectory, options, cancellationToken);
    }

    // ── Dispatch ──────────────────────────────────────────────────────

    private static async Task<MonitoringRuntimeEventSnapshot> ReadInternalAsync(
        string runDirectory,
        RuntimeEventArtifactReadOptions options,
        CancellationToken cancellationToken)
    {
        var path = GetEventFilePath(runDirectory);

        if (!File.Exists(path))
        {
            return EmptySnapshot(options.RunId, ResolveCap(options.Cap));
        }

        long fileLength = 0;
        try
        {
            fileLength = new FileInfo(path).Length;
        }
        catch (IOException)
        {
            // Fall through to the streamed path, which tolerates read errors.
        }
        catch (UnauthorizedAccessException)
        {
            // Fall through to the streamed path, which tolerates read errors.
        }

        if (fileLength > options.MaxFileBytes)
        {
            return await ReadTailAsync(path, options, cancellationToken)
                .ConfigureAwait(false);
        }

        return await ReadStreamAsync(path, options, cancellationToken)
            .ConfigureAwait(false);
    }

    // ── Streamed read (normal path) ───────────────────────────────────

    private static async Task<MonitoringRuntimeEventSnapshot> ReadStreamAsync(
        string path,
        RuntimeEventArtifactReadOptions options,
        CancellationToken cancellationToken)
    {
        var cap = ResolveCap(options.Cap);
        var window = new Queue<MonitoringRuntimeEvent>();
        var totalAvailable = 0;
        var nextIndex = 0;

        try
        {
            using var stream = OpenForSharedRead(path);
            using var reader = new StreamReader(stream);

            string? line;
            while ((line = await reader
                        .ReadLineAsync(cancellationToken)
                        .ConfigureAwait(false)) is not null)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    // Blank lines are not events; skip without consuming an index.
                    continue;
                }

                totalAvailable++;
                window.Enqueue(ParseLine(line, nextIndex));
                nextIndex++;

                // Rolling window: keep only the newest `cap` entries.
                if (window.Count > cap)
                {
                    window.Dequeue();
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (IOException)
        {
            // Best-effort: a vanishing or partially-flushed file may raise a read
            // error mid-scan. Return whatever we collected instead of failing the
            // whole monitoring snapshot.
        }

        var truncated = totalAvailable > cap;

        return new MonitoringRuntimeEventSnapshot
        {
            RunId = options.RunId,
            Events = window.ToArray(),
            Cap = cap,
            Truncated = truncated,
            TotalAvailable = totalAvailable,
        };
    }

    // ── Tail read (large-file path) ───────────────────────────────────

    private static async Task<MonitoringRuntimeEventSnapshot> ReadTailAsync(
        string path,
        RuntimeEventArtifactReadOptions options,
        CancellationToken cancellationToken)
    {
        var entries = new List<MonitoringRuntimeEvent>();
        var cap = ResolveCap(options.Cap);

        try
        {
            using var stream = OpenForSharedRead(path);
            var start = Math.Max(0, stream.Length - options.TailReadBytes);

            using var reader = new StreamReader(stream);

            if (start > 0)
            {
                // Seek into the tail, then skip the (likely partial) first line so
                // we only parse whole lines.
                stream.Position = start;
                _ = await reader
                    .ReadLineAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            var nextIndex = 0;
            string? line;
            while ((line = await reader
                        .ReadLineAsync(cancellationToken)
                        .ConfigureAwait(false)) is not null)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                entries.Add(ParseLine(line, nextIndex));
                nextIndex++;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (IOException)
        {
            // Best-effort: return the partial tail collected so far.
        }

        // Apply the newest-event cap to the tail window.
        IReadOnlyList<MonitoringRuntimeEvent> events = entries;
        if (entries.Count > cap)
        {
            events = entries.GetRange(entries.Count - cap, cap);
        }

        // The full file was not read, so the true total is unknown. Signal
        // truncation so clients render a "showing latest available" affordance.
        return new MonitoringRuntimeEventSnapshot
        {
            RunId = options.RunId,
            Events = events,
            Cap = cap,
            Truncated = true,
            TotalAvailable = null,
        };
    }

    // ── Line parsing (lenient per-field extraction) ───────────────────

    /// <summary>
    /// Parse a single non-blank line into a <see cref="MonitoringRuntimeEvent"/>.
    ///
    /// Invalid JSON or a non-object root yields a
    /// <see cref="RuntimeEventParseStatus.Malformed"/> entry that retains the raw
    /// line and a parse error. A valid JSON object that lacks a non-empty
    /// <c>eventType</c> yields <see cref="RuntimeEventParseStatus.MissingFields"/>.
    /// Individual bad field values (e.g. an unparseable timestamp or severity) never
    /// discard the whole entry — the offending field is left null and the rest is
    /// surfaced, preserving maximum debug data for the UI.
    /// </summary>
    /// <param name="line">The raw line (non-blank).</param>
    /// <param name="index">0-based ordinal for the entry.</param>
    private static MonitoringRuntimeEvent ParseLine(string line, int index)
    {
        if (line.Length > MaxLineChars)
        {
            return MonitoringRuntimeEvent.FromMalformedLine(
                index,
                TruncateRaw(line),
                $"Line length {line.Length} exceeds per-line cap {MaxLineChars}; not parsed.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException ex)
        {
            return MonitoringRuntimeEvent.FromMalformedLine(index, line, ex.Message);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return MonitoringRuntimeEvent.FromMalformedLine(
                    index,
                    line,
                    $"Expected a JSON object, got {root.ValueKind}.");
            }

            var eventType = GetString(root, "eventType");
            var hasType = !string.IsNullOrWhiteSpace(eventType);

            return new MonitoringRuntimeEvent
            {
                Index = index,
                Status = hasType
                    ? RuntimeEventParseStatus.Valid
                    : RuntimeEventParseStatus.MissingFields,
                EventId = NullIfEmpty(GetString(root, "eventId")),
                RunId = NullIfEmpty(GetString(root, "runId")),
                RuntimeRunId = NullIfEmpty(GetString(root, "runtimeRunId")),
                Sequence = GetNullableInt(root, "sequence"),
                OccurredAt = GetNullableDateTimeOffset(root, "occurredAt"),
                EventType = hasType ? eventType : null,
                Severity = GetNullableSeverity(root, "severity"),
                Message = GetString(root, "message"),
                Payload = GetPayload(root, "payload"),
                RawLine = line,
            };
        }
    }

    private static string? GetString(JsonElement root, string name)
        => root.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

    private static int? GetNullableInt(JsonElement root, string name)
        => root.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var i)
                ? i
                : null;

    private static DateTimeOffset? GetNullableDateTimeOffset(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = value.GetString();
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return DateTimeOffset.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var dto)
            ? dto
            : null;
    }

    private static EventSeverity? GetNullableSeverity(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString();
            if (!string.IsNullOrWhiteSpace(text)
                && Enum.TryParse(text, ignoreCase: true, out EventSeverity sev)
                && Enum.IsDefined(sev))
            {
                return sev;
            }

            return null;
        }

        if (value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var n)
            && Enum.IsDefined((EventSeverity)n))
        {
            return (EventSeverity)n;
        }

        return null;
    }

    private static Dictionary<string, object?>? GetPayload(
        JsonElement root,
        string name)
    {
        if (!root.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        try
        {
            // Deserialize from raw text so the returned nodes own their data
            // independently of the (disposed) JsonDocument buffer.
            var obj = JsonSerializer.Deserialize<JsonObject>(value.GetRawText());
            if (obj is null || obj.Count == 0)
            {
                return null;
            }

            var payload = new Dictionary<string, object?>(obj.Count);
            foreach (var entry in obj)
            {
                payload[entry.Key] = entry.Value;
            }

            return payload;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────

    private static FileStream OpenForSharedRead(string path)
        => new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096,
            useAsync: true);

    private static MonitoringRuntimeEventSnapshot EmptySnapshot(string? runId, int cap) => new()
    {
        RunId = runId,
        Events = [],
        Cap = cap,
        TotalAvailable = 0,
    };

    /// <summary>
    /// Resolve the effective newest-event cap. A <c>null</c> option applies
    /// <see cref="DefaultCap"/> so reads are bounded by default and local sync is
    /// never flooded by a high-volume stream. There is no unbounded default: an
    /// explicit large value is honored as-is only when the caller sets it.
    /// </summary>
    private static int ResolveCap(int? cap) => cap ?? DefaultCap;

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static string TruncateRaw(string line)
    {
        if (line.Length <= MaxRawLineChars)
        {
            return line;
        }

        return string.Concat(line.AsSpan(0, MaxRawLineChars), "…[truncated]");
    }
}

/// <summary>
/// Options for <see cref="RuntimeEventArtifactReader.ReadAsync"/>.
/// </summary>
public sealed record RuntimeEventArtifactReadOptions
{
    /// <summary>
    /// Optional run identifier used to tag
    /// <see cref="MonitoringRuntimeEventSnapshot.RunId"/>. Per-event <c>runId</c>
    /// values come from the stream itself.
    /// </summary>
    public string? RunId { get; init; }

    /// <summary>
    /// Newest-event cap. Only the newest <c>Cap</c> entries are returned.
    /// <c>null</c> (default) applies <see cref="RuntimeEventArtifactReader.DefaultCap"/>.
    /// </summary>
    public int? Cap { get; init; }

    /// <summary>
    /// Files larger than this are served from a bounded tail window instead of being
    /// streamed in full. Defaults to
    /// <see cref="RuntimeEventArtifactReader.DefaultMaxFileBytes"/>.
    /// </summary>
    public long MaxFileBytes { get; init; } = RuntimeEventArtifactReader.DefaultMaxFileBytes;

    /// <summary>
    /// Tail-window size in bytes used when a file exceeds <see cref="MaxFileBytes"/>.
    /// Defaults to <see cref="RuntimeEventArtifactReader.DefaultTailReadBytes"/>.
    /// </summary>
    public long TailReadBytes { get; init; } = RuntimeEventArtifactReader.DefaultTailReadBytes;
}

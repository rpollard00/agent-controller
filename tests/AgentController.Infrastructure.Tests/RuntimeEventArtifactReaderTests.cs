using System.Globalization;
using System.Text;
using AgentController.Domain;
using AgentController.Infrastructure;

namespace AgentController.Infrastructure.Tests;

/// <summary>
/// Tests for <see cref="RuntimeEventArtifactReader"/> covering file absence,
/// valid parsing, malformed lines, missing fields, partial writes, blank lines,
/// newest-event caps, and the large-file tail path.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "IAsyncLifetime.DisposeAsync disposes all owned fields.")]
public class RuntimeEventArtifactReaderTests : IAsyncLifetime
{
    private string _tempRoot = null!;
    private string _runDirectory = null!;

    public Task InitializeAsync()
    {
        _tempRoot = Path.Combine(
            Path.GetTempPath(),
            "event-reader-test-" + Guid.NewGuid().ToString("N"));
        _runDirectory = Path.Combine(_tempRoot, "run_abc");
        Directory.CreateDirectory(_runDirectory);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (_tempRoot is not null && Directory.Exists(_tempRoot))
        {
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        return Task.CompletedTask;
    }

    private string EventFilePath => RuntimeEventArtifactReader.GetEventFilePath(_runDirectory);

    private void WriteEventsFile(params string[] lines)
    {
        var eventsDir = Path.Combine(_runDirectory, "events");
        Directory.CreateDirectory(eventsDir);

        // An empty argument list yields a truly empty file; otherwise join with
        // newlines and add a final newline, matching an append-style writer.
        var text = lines.Length == 0 ? "" : string.Join("\n", lines) + "\n";
        File.WriteAllText(EventFilePath, text);
    }

    private static string Line(string eventType, int sequence, string? message = null) =>
        "{\"eventId\":\"evt_" + sequence.ToString(CultureInfo.InvariantCulture) + "\","
        + "\"runId\":\"run_abc\","
        + "\"eventType\":\"" + eventType + "\","
        + "\"sequence\":" + sequence.ToString(CultureInfo.InvariantCulture) + ","
        + "\"occurredAt\":\"2026-06-17T12:00:0" + (sequence % 10).ToString(CultureInfo.InvariantCulture) + "Z\","
        + "\"severity\":\"warning\","
        + "\"message\":\"" + (message ?? "msg-" + sequence.ToString(CultureInfo.InvariantCulture)) + "\","
        + "\"payload\":{\"phase\":\"validation\",\"count\":" + sequence.ToString(CultureInfo.InvariantCulture) + "}}";

    // ── Path resolution ───────────────────────────────────────────────

    [Fact]
    public void GetEventFilePath_ResolvesCanonicalPath()
    {
        var path = RuntimeEventArtifactReader.GetEventFilePath(_runDirectory);

        Assert.Equal(
            Path.Combine(_runDirectory, "events", "events.jsonl"),
            path);
    }

    // ── Absent / empty file ───────────────────────────────────────────

    [Fact]
    public async Task ReadAsync_AbsentFile_ReturnsEmptySnapshot()
    {
        // No events/ directory or file created.
        var snapshot = await RuntimeEventArtifactReader.ReadAsync(_runDirectory);

        Assert.NotNull(snapshot.Events);
        Assert.Empty(snapshot.Events);
        Assert.Equal(0, snapshot.TotalAvailable);
        Assert.False(snapshot.Truncated);
        // The default cap is still reported as the effective cap even when the
        // stream is absent, so clients render a consistent "last N" affordance.
        Assert.Equal(RuntimeEventArtifactReader.DefaultCap, snapshot.Cap);
    }

    [Fact]
    public async Task ReadAsync_EmptyFile_ReturnsEmptySnapshot()
    {
        WriteEventsFile(Array.Empty<string>());

        var snapshot = await RuntimeEventArtifactReader.ReadAsync(_runDirectory);

        Assert.Empty(snapshot.Events);
        Assert.Equal(0, snapshot.TotalAvailable);
        Assert.False(snapshot.Truncated);
    }

    [Fact]
    public async Task ReadAsync_EmptyRunDirectory_ReturnsEmptySnapshot()
    {
        var snapshot = await RuntimeEventArtifactReader.ReadAsync("");

        Assert.Empty(snapshot.Events);
    }

    [Fact]
    public async Task ReadAsync_NullRunDirectory_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => RuntimeEventArtifactReader.ReadAsync(null!, CancellationToken.None));
    }

    // ── Valid parsing ─────────────────────────────────────────────────

    [Fact]
    public async Task ReadAsync_ValidLines_ParseFieldsAndPreserveRaw()
    {
        WriteEventsFile(new[]
        {
            Line(RuntimeEventTypes.Accepted, 0),
            Line(RuntimeEventTypes.Status, 1),
        });

        var snapshot = await RuntimeEventArtifactReader.ReadAsync(_runDirectory);

        Assert.Equal(2, snapshot.Events.Count);
        Assert.Equal(2, snapshot.TotalAvailable);
        Assert.False(snapshot.Truncated);

        var first = snapshot.Events[0];
        Assert.Equal(0, first.Index);
        Assert.Equal(RuntimeEventParseStatus.Valid, first.Status);
        Assert.Equal("evt_0", first.EventId);
        Assert.Equal("run_abc", first.RunId);
        Assert.Equal(RuntimeEventTypes.Accepted, first.EventType);
        Assert.Equal(0, first.Sequence);
        Assert.Equal(EventSeverity.Warning, first.Severity);
        Assert.Equal("msg-0", first.Message);
        Assert.NotNull(first.OccurredAt);
        Assert.Equal(
            new DateTimeOffset(2026, 6, 17, 12, 0, 0, TimeSpan.Zero),
            first.OccurredAt!.Value);
        Assert.NotNull(first.Payload);
        Assert.True(first.Payload!.ContainsKey("phase"));
        Assert.NotNull(first.RawLine);
        Assert.Null(first.ParseError);
    }

    [Fact]
    public async Task ReadAsync_PreservesOldestFirstOrderAndIndices()
    {
        WriteEventsFile(new[]
        {
            Line(RuntimeEventTypes.Accepted, 0),
            Line(RuntimeEventTypes.Heartbeat, 1),
            Line(RuntimeEventTypes.Status, 2),
        });

        var snapshot = await RuntimeEventArtifactReader.ReadAsync(_runDirectory);

        Assert.Equal(3, snapshot.Events.Count);
        Assert.Equal(0, snapshot.Events[0].Index);
        Assert.Equal(1, snapshot.Events[1].Index);
        Assert.Equal(2, snapshot.Events[2].Index);
        Assert.Equal(RuntimeEventTypes.Accepted, snapshot.Events[0].EventType);
        Assert.Equal(RuntimeEventTypes.Status, snapshot.Events[2].EventType);
    }

    [Fact]
    public async Task ReadAsync_NumericSeverity_Parses()
    {
        WriteEventsFile("{\"eventType\":\"runtime.status\",\"severity\":2}");

        var snapshot = await RuntimeEventArtifactReader.ReadAsync(_runDirectory);

        var entry = Assert.Single(snapshot.Events);
        Assert.Equal(EventSeverity.Error, entry.Severity);
    }

    [Fact]
    public async Task ReadAsync_RunIdOption_TagsSnapshot()
    {
        WriteEventsFile(new[] { Line(RuntimeEventTypes.Accepted, 0) });

        var snapshot = await RuntimeEventArtifactReader.ReadAsync(
            _runDirectory,
            new RuntimeEventArtifactReadOptions { RunId = "run_tagged" },
            CancellationToken.None);

        Assert.Equal("run_tagged", snapshot.RunId);
    }

    // ── Malformed / invalid ───────────────────────────────────────────

    [Fact]
    public async Task ReadAsync_InvalidJsonLine_BecomesMalformed_WithRawLine()
    {
        WriteEventsFile(new[]
        {
            Line(RuntimeEventTypes.Accepted, 0),
            "{not valid json",
            Line(RuntimeEventTypes.Status, 2),
        });

        var snapshot = await RuntimeEventArtifactReader.ReadAsync(_runDirectory);

        Assert.Equal(3, snapshot.Events.Count);
        Assert.Equal(3, snapshot.TotalAvailable);

        var malformed = snapshot.Events[1];
        Assert.Equal(1, malformed.Index);
        Assert.Equal(RuntimeEventParseStatus.Malformed, malformed.Status);
        Assert.Equal("{not valid json", malformed.RawLine);
        Assert.NotNull(malformed.ParseError);

        // Surrounding valid entries keep their positions.
        Assert.Equal(RuntimeEventParseStatus.Valid, snapshot.Events[0].Status);
        Assert.Equal(RuntimeEventParseStatus.Valid, snapshot.Events[2].Status);
    }

    [Theory]
    [InlineData("[1, 2, 3]", "Array")]
    [InlineData("42", "Number")]
    [InlineData("\"hello\"", "String")]
    public async Task ReadAsync_NonObjectRoot_BecomesMalformed(
        string rawLine,
        string expectedKind)
    {
        WriteEventsFile(new[] { rawLine });

        var snapshot = await RuntimeEventArtifactReader.ReadAsync(_runDirectory);

        var entry = Assert.Single(snapshot.Events);
        Assert.Equal(RuntimeEventParseStatus.Malformed, entry.Status);
        Assert.Equal(rawLine, entry.RawLine);
        Assert.Contains(expectedKind, entry.ParseError ?? string.Empty);
    }

    [Fact]
    public async Task ReadAsync_MissingEventType_BecomesMissingFields()
    {
        WriteEventsFile("{\"eventId\":\"evt_x\",\"runId\":\"run_abc\",\"message\":\"no type\"}");

        var snapshot = await RuntimeEventArtifactReader.ReadAsync(_runDirectory);

        var entry = Assert.Single(snapshot.Events);
        Assert.Equal(RuntimeEventParseStatus.MissingFields, entry.Status);
        Assert.Null(entry.EventType);
        // Present fields are still surfaced.
        Assert.Equal("evt_x", entry.EventId);
        Assert.Equal("run_abc", entry.RunId);
        Assert.Equal("no type", entry.Message);
        Assert.NotNull(entry.RawLine);
    }

    [Fact]
    public async Task ReadAsync_BadFieldValues_DoNotDiscardEntry()
    {
        // A valid object with an unparseable timestamp, an unknown severity,
        // and a non-integer sequence: entry stays Valid (eventType present) with
        // the bad fields left null rather than being dropped.
        WriteEventsFile(new[]
        {
            "{\"eventType\":\"runtime.status\","
            + "\"occurredAt\":\"not-a-date\","
            + "\"severity\":\"fatal\","
            + "\"sequence\":\"oops\"}",
        });

        var snapshot = await RuntimeEventArtifactReader.ReadAsync(_runDirectory);

        var entry = Assert.Single(snapshot.Events);
        Assert.Equal(RuntimeEventParseStatus.Valid, entry.Status);
        Assert.Null(entry.OccurredAt);
        Assert.Null(entry.Severity);
        Assert.Null(entry.Sequence);
    }

    [Fact]
    public async Task ReadAsync_PartialLastLine_BecomesMalformed()
    {
        // Simulate a runtime mid-write: the last line is truncated mid-object.
        var content =
            Line(RuntimeEventTypes.Accepted, 0) + "\n"
            + Line(RuntimeEventTypes.Heartbeat, 1) + "\n"
            + "{\"eventType\":\"runtime.status\",\"sequence\":9,\"message\":\"partia";

        var eventsDir = Path.Combine(_runDirectory, "events");
        Directory.CreateDirectory(eventsDir);
        await File.WriteAllTextAsync(EventFilePath, content); // no trailing newline

        var snapshot = await RuntimeEventArtifactReader.ReadAsync(_runDirectory);

        Assert.Equal(3, snapshot.Events.Count);
        Assert.Equal(3, snapshot.TotalAvailable);
        Assert.Equal(2, snapshot.Events.Count(e => e.Status == RuntimeEventParseStatus.Valid));

        var malformed = Assert.Single(
            snapshot.Events,
            e => e.Status == RuntimeEventParseStatus.Malformed);
        Assert.Contains("partia", malformed.RawLine);
        Assert.NotNull(malformed.ParseError);
    }

    [Fact]
    public async Task ReadAsync_BlankLines_AreSkippedWithoutConsumingIndex()
    {
        WriteEventsFile(new[]
        {
            Line(RuntimeEventTypes.Accepted, 0),
            "",
            "   ",
            Line(RuntimeEventTypes.Status, 1),
        });

        var snapshot = await RuntimeEventArtifactReader.ReadAsync(_runDirectory);

        // Two real events; blank lines neither counted nor indexed.
        Assert.Equal(2, snapshot.Events.Count);
        Assert.Equal(2, snapshot.TotalAvailable);
        Assert.Equal(0, snapshot.Events[0].Index);
        Assert.Equal(1, snapshot.Events[1].Index);
    }

    // ── Cap behavior ──────────────────────────────────────────────────

    [Fact]
    public async Task ReadAsync_Cap_ReturnsNewestEntries_Truncated()
    {
        var lines = Enumerable.Range(0, 10)
            .Select(i => Line(RuntimeEventTypes.Status, i))
            .ToArray();

        WriteEventsFile(lines);

        var snapshot = await RuntimeEventArtifactReader.ReadAsync(
            _runDirectory,
            new RuntimeEventArtifactReadOptions { Cap = 3 },
            CancellationToken.None);

        Assert.Equal(3, snapshot.Events.Count);
        Assert.Equal(10, snapshot.TotalAvailable);
        Assert.True(snapshot.Truncated);
        Assert.Equal(3, snapshot.Cap);

        // Newest 3 (indices 7,8,9), oldest-first within the window.
        Assert.Equal(7, snapshot.Events[0].Index);
        Assert.Equal(7, snapshot.Events[0].Sequence);
        Assert.Equal(9, snapshot.Events[2].Index);
        Assert.Equal(9, snapshot.Events[2].Sequence);
    }

    [Fact]
    public async Task ReadAsync_DefaultOptions_BelowDefaultCap_ReturnsAll_ReportsDefaultCap()
    {
        // A null (default) cap applies DefaultCap, not "unbounded". With fewer
        // events than DefaultCap, all are returned (not truncated), but the
        // snapshot reports the effective default cap.
        var lines = Enumerable.Range(0, 5)
            .Select(i => Line(RuntimeEventTypes.Status, i))
            .ToArray();

        WriteEventsFile(lines);

        var snapshot = await RuntimeEventArtifactReader.ReadAsync(
            _runDirectory,
            new RuntimeEventArtifactReadOptions { Cap = null },
            CancellationToken.None);

        Assert.Equal(5, snapshot.Events.Count);
        Assert.False(snapshot.Truncated);
        Assert.Equal(RuntimeEventArtifactReader.DefaultCap, snapshot.Cap);
        Assert.Equal(5, snapshot.TotalAvailable);
    }

    [Fact]
    public async Task ReadAsync_DefaultOptions_AboveDefaultCap_ReturnsNewestDefaultCap_Truncated()
    {
        // Regression guard for the capped-by-default requirement: a default read
        // (no cap specified) must return only the newest DefaultCap events with
        // Truncated=true, never the entire file. This bounds local sync memory and
        // payload size for high-volume streams.
        var total = RuntimeEventArtifactReader.DefaultCap + 50;

        var lines = Enumerable.Range(0, total)
            .Select(i => Line(RuntimeEventTypes.Status, i))
            .ToArray();

        WriteEventsFile(lines);

        var snapshot = await RuntimeEventArtifactReader.ReadAsync(_runDirectory);

        Assert.Equal(RuntimeEventArtifactReader.DefaultCap, snapshot.Events.Count);
        Assert.True(snapshot.Truncated);
        Assert.Equal(total, snapshot.TotalAvailable);
        Assert.Equal(RuntimeEventArtifactReader.DefaultCap, snapshot.Cap);

        // Newest DefaultCap entries (indices total-DefaultCap .. total-1),
        // oldest-first within the kept window.
        Assert.Equal(total - RuntimeEventArtifactReader.DefaultCap, snapshot.Events[0].Index);
        Assert.Equal(total - RuntimeEventArtifactReader.DefaultCap, snapshot.Events[0].Sequence);
        Assert.Equal(total - 1, snapshot.Events[^1].Index);
        Assert.Equal(total - 1, snapshot.Events[^1].Sequence);
    }

    [Fact]
    public async Task ReadAsync_CapLargerThanCount_ReturnsAll_NotTruncated()
    {
        WriteEventsFile(new[]
        {
            Line(RuntimeEventTypes.Accepted, 0),
            Line(RuntimeEventTypes.Status, 1),
        });

        var snapshot = await RuntimeEventArtifactReader.ReadAsync(
            _runDirectory,
            new RuntimeEventArtifactReadOptions { Cap = 50 },
            CancellationToken.None);

        Assert.Equal(2, snapshot.Events.Count);
        Assert.False(snapshot.Truncated);
        Assert.Equal(2, snapshot.TotalAvailable);
    }

    // ── Large file (tail path) ────────────────────────────────────────

    [Fact]
    public async Task ReadAsync_FileExceedingMaxBytes_ReadsTailAndMarksTruncated()
    {
        // Fixed-length lines so byte offsets are predictable.
        var lines = Enumerable.Range(0, 5)
            .Select(MakeFixedLine)
            .ToArray();

        var eventsDir = Path.Combine(_runDirectory, "events");
        Directory.CreateDirectory(eventsDir);
        var bytes = Encoding.UTF8.GetBytes(string.Join("\n", lines) + "\n");
        await File.WriteAllBytesAsync(EventFilePath, bytes);

        var lineLen = Encoding.UTF8.GetByteCount(lines[0]) + 1; // include '\n'
        var options = new RuntimeEventArtifactReadOptions
        {
            Cap = 10,
            MaxFileBytes = 0, // force the tail path
            TailReadBytes = 2 * lineLen + 2, // land mid-line so the fragment is discarded
        };

        var snapshot = await RuntimeEventArtifactReader.ReadAsync(_runDirectory, options, CancellationToken.None);

        // Did not read the whole file: truncated with unknown total.
        Assert.True(snapshot.Truncated);
        Assert.Null(snapshot.TotalAvailable);

        // The tail window contains only the last two complete events.
        Assert.Equal(2, snapshot.Events.Count);
        Assert.Equal(3, snapshot.Events[0].Sequence);
        Assert.Equal(4, snapshot.Events[1].Sequence);
    }

    [Fact]
    public async Task ReadAsync_DefaultOptions_LargeFile_TailIsCappedToDefaultCap()
    {
        // The default cap must also bound the large-file tail path so local
        // sync is never flooded, even for a pathologically large log whose tail
        // window holds more than DefaultCap complete lines.
        var total = RuntimeEventArtifactReader.DefaultCap + 50;

        var lines = Enumerable.Range(0, total)
            .Select(MakeFixedLine)
            .ToArray();

        var eventsDir = Path.Combine(_runDirectory, "events");
        Directory.CreateDirectory(eventsDir);
        var bytes = Encoding.UTF8.GetBytes(string.Join("\n", lines) + "\n");
        await File.WriteAllBytesAsync(EventFilePath, bytes);

        // Force the tail path; TailReadBytes >= file length reads the whole tail
        // window, so without the cap this would return all `total` events.
        var options = new RuntimeEventArtifactReadOptions
        {
            MaxFileBytes = 0,
            TailReadBytes = bytes.LongLength + 16,
        };

        var snapshot = await RuntimeEventArtifactReader.ReadAsync(_runDirectory, options, CancellationToken.None);

        Assert.True(snapshot.Truncated);
        Assert.Null(snapshot.TotalAvailable);
        Assert.Equal(RuntimeEventArtifactReader.DefaultCap, snapshot.Cap);
        Assert.Equal(RuntimeEventArtifactReader.DefaultCap, snapshot.Events.Count);

        // Newest DefaultCap entries (sequences total-DefaultCap .. total-1).
        Assert.Equal(total - RuntimeEventArtifactReader.DefaultCap, snapshot.Events[0].Sequence);
        Assert.Equal(total - 1, snapshot.Events[^1].Sequence);
    }

    private static string MakeFixedLine(int i)
    {
        var seq = i.ToString("D3", CultureInfo.InvariantCulture);
        return "{\"eventType\":\"runtime.status\",\"sequence\":"
            + i.ToString(CultureInfo.InvariantCulture)
            + ",\"message\":\"event-" + seq + "\"}";
    }

    // ── Runaway line guard ────────────────────────────────────────────

    [Fact]
    public async Task ReadAsync_OversizeLine_BecomesMalformedWithTruncatedRaw()
    {
        var oversizedMessage = new string('q', RuntimeEventArtifactReader.MaxLineChars + 10);
        WriteEventsFile(new[]
        {
            "{\"eventType\":\"runtime.status\",\"message\":\"" + oversizedMessage + "\"}",
        });

        var snapshot = await RuntimeEventArtifactReader.ReadAsync(_runDirectory);

        var entry = Assert.Single(snapshot.Events);
        Assert.Equal(RuntimeEventParseStatus.Malformed, entry.Status);
        Assert.NotNull(entry.ParseError);
        Assert.Contains("exceeds", entry.ParseError!, StringComparison.Ordinal);
        // Raw line is truncated to keep the snapshot bounded.
        Assert.True(entry.RawLine!.Length <= RuntimeEventArtifactReader.MaxRawLineChars + 16);
        Assert.EndsWith("[truncated]", entry.RawLine);
    }
}

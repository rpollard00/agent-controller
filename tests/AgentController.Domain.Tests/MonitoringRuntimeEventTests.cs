namespace AgentController.Domain.Tests;

public class MonitoringRuntimeEventTests
{
    // ── Enum shape (additive / backward compatible) ───────────────────

    [Fact]
    public void RuntimeEventParseStatus_HasExpectedValues()
    {
        // Valid is 0 so default(MonitoringRuntimeEvent) is a sensible "valid" baseline.
        Assert.Equal(0, (int)RuntimeEventParseStatus.Valid);
        Assert.Equal(1, (int)RuntimeEventParseStatus.MissingFields);
        Assert.Equal(2, (int)RuntimeEventParseStatus.Malformed);

        var names = Enum.GetNames<RuntimeEventParseStatus>();
        Assert.Contains("Valid", names);
        Assert.Contains("MissingFields", names);
        Assert.Contains("Malformed", names);
    }

    // ── Valid case ────────────────────────────────────────────────────

    [Fact]
    public void FromRuntimeEvent_FullEnvelope_IsValid_AndPreservesParsedFields()
    {
        var occurredAt = new DateTimeOffset(2026, 6, 17, 12, 0, 0, TimeSpan.Zero);
        var source = new RuntimeEvent
        {
            EventId = "evt_001",
            RunId = "run_123",
            RuntimeRunId = "pi_456",
            Sequence = 7,
            OccurredAt = occurredAt,
            EventType = RuntimeEventTypes.Status,
            Severity = EventSeverity.Warning,
            Message = "Running unit tests",
            Payload = new Dictionary<string, object?>
            {
                ["phase"] = "validation",
                ["testCount"] = 42,
            },
        };

        var dto = MonitoringRuntimeEvent.FromRuntimeEvent(source, index: 3, rawLine: "{...}");

        Assert.Equal(3, dto.Index);
        Assert.Equal(RuntimeEventParseStatus.Valid, dto.Status);
        Assert.Equal("evt_001", dto.EventId);
        Assert.Equal("run_123", dto.RunId);
        Assert.Equal("pi_456", dto.RuntimeRunId);
        Assert.Equal(7, dto.Sequence);
        Assert.Equal(occurredAt, dto.OccurredAt);
        Assert.Equal(RuntimeEventTypes.Status, dto.EventType);
        Assert.Equal(EventSeverity.Warning, dto.Severity);
        Assert.Equal("Running unit tests", dto.Message);
        Assert.NotNull(dto.Payload);
        Assert.Equal("validation", dto.Payload!["phase"]);
        Assert.Equal(42, dto.Payload!["testCount"]);
        Assert.Equal("{...}", dto.RawLine);
        Assert.Null(dto.ParseError);
    }

    [Fact]
    public void FromRuntimeEvent_MinimalEnvelope_IsValid_WhenEventTypePresent()
    {
        var source = new RuntimeEvent
        {
            EventId = "evt_002",
            RunId = "run_123",
            EventType = RuntimeEventTypes.Heartbeat,
        };

        var dto = MonitoringRuntimeEvent.FromRuntimeEvent(source, index: 0);

        Assert.Equal(RuntimeEventParseStatus.Valid, dto.Status);
        Assert.Equal(RuntimeEventTypes.Heartbeat, dto.EventType);
        // Optional fields default to null on the DTO even though the domain
        // record defaults Severity to Info; monitoring keeps unknowns null.
        Assert.Null(dto.RuntimeRunId);
        Assert.Null(dto.Sequence);
        Assert.Null(dto.Message);
        Assert.Null(dto.Payload);
        Assert.Null(dto.RawLine);
        Assert.Null(dto.ParseError);
    }

    [Fact]
    public void FromRuntimeEvent_OmitsRawLine_ByDefault()
    {
        var dto = MonitoringRuntimeEvent.FromRuntimeEvent(
            new RuntimeEvent { EventId = "e", RunId = "r", EventType = RuntimeEventTypes.Accepted },
            index: 0);

        Assert.Null(dto.RawLine);
    }

    // ── Missing case ──────────────────────────────────────────────────

    [Fact]
    public void FromRuntimeEvent_MissingEventType_IsMissingFields()
    {
        // Parsed successfully as an object, but the identifying EventType is absent.
        var source = new RuntimeEvent
        {
            EventId = "evt_003",
            RunId = "run_123",
            EventType = string.Empty, // missing the required classifying field
            Message = "partial entry",
        };

        var dto = MonitoringRuntimeEvent.FromRuntimeEvent(source, index: 1, rawLine: "{}");

        Assert.Equal(RuntimeEventParseStatus.MissingFields, dto.Status);
        Assert.Null(dto.EventType);
        // Parsed fields that were present are still surfaced for debugging.
        Assert.Equal("evt_003", dto.EventId);
        Assert.Equal("run_123", dto.RunId);
        Assert.Equal("partial entry", dto.Message);
        Assert.Equal("{}", dto.RawLine);
        Assert.Null(dto.ParseError);
    }

    [Fact]
    public void FromRuntimeEvent_WhitespaceOnlyEventType_IsMissingFields()
    {
        var source = new RuntimeEvent
        {
            RunId = "run_123",
            EventType = "   ",
        };

        var dto = MonitoringRuntimeEvent.FromRuntimeEvent(source, index: 0);

        Assert.Equal(RuntimeEventParseStatus.MissingFields, dto.Status);
        Assert.Null(dto.EventType);
    }

    [Fact]
    public void FromRuntimeEvent_NormalizesBlankIdentifiers_ToNull()
    {
        var dto = MonitoringRuntimeEvent.FromRuntimeEvent(
            new RuntimeEvent
            {
                EventId = "  ",
                RunId = " ",
                RuntimeRunId = "",
                EventType = RuntimeEventTypes.Status,
            },
            index: 0);

        Assert.Equal(RuntimeEventParseStatus.Valid, dto.Status);
        Assert.Null(dto.EventId);
        Assert.Null(dto.RunId);
        Assert.Null(dto.RuntimeRunId);
    }

    // ── Malformed case ────────────────────────────────────────────────

    [Fact]
    public void FromMalformedLine_PreservesRawLineAndParseError()
    {
        var dto = MonitoringRuntimeEvent.FromMalformedLine(
            index: 5,
            rawLine: "{not valid json",
            parseError: "Expected ':' after property name.");

        Assert.Equal(5, dto.Index);
        Assert.Equal(RuntimeEventParseStatus.Malformed, dto.Status);
        Assert.Equal("{not valid json", dto.RawLine);
        Assert.Equal("Expected ':' after property name.", dto.ParseError);
        // No parsed fields are available for a malformed entry.
        Assert.Null(dto.EventId);
        Assert.Null(dto.RunId);
        Assert.Null(dto.EventType);
        Assert.Null(dto.OccurredAt);
        Assert.Null(dto.Severity);
        Assert.Null(dto.Message);
        Assert.Null(dto.Payload);
    }

    [Fact]
    public void FromMalformedLine_DefaultsHaveNoParsedPayload()
    {
        var dto = MonitoringRuntimeEvent.FromMalformedLine(0, "garbage", "bad");

        Assert.Null(dto.Sequence);
        Assert.Null(dto.Payload);
    }

    // ── Record equality / immutability ────────────────────────────────

    [Fact]
    public void MonitoringRuntimeEvent_IsImmutableAndValueEqual()
    {
        // Pin OccurredAt: RuntimeEvent.OccurredAt otherwise defaults to
        // DateTimeOffset.UtcNow at construction, which would differ per instance.
        var occurredAt = new DateTimeOffset(2026, 6, 17, 12, 0, 0, TimeSpan.Zero);
        var a = MonitoringRuntimeEvent.FromRuntimeEvent(
            new RuntimeEvent
            {
                EventId = "e1",
                RunId = "r1",
                EventType = RuntimeEventTypes.Status,
                OccurredAt = occurredAt,
            },
            index: 2,
            rawLine: "raw");
        var b = MonitoringRuntimeEvent.FromRuntimeEvent(
            new RuntimeEvent
            {
                EventId = "e1",
                RunId = "r1",
                EventType = RuntimeEventTypes.Status,
                OccurredAt = occurredAt,
            },
            index: 2,
            rawLine: "raw");

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    // ── Stream container ──────────────────────────────────────────────

    [Fact]
    public void MonitoringRuntimeEventSnapshot_DefaultsAreClientSafe()
    {
        var stream = new MonitoringRuntimeEventSnapshot();

        Assert.Null(stream.RunId);
        Assert.NotNull(stream.Events);
        Assert.Empty(stream.Events);
        Assert.Null(stream.Cap);
        Assert.False(stream.Truncated);
        Assert.Null(stream.TotalAvailable);
        // GeneratedAt defaults to "now" (roughly), not default(DateTimeOffset).
        Assert.True(stream.GeneratedAt <= DateTimeOffset.UtcNow.AddSeconds(1));
        Assert.True(stream.GeneratedAt > DateTimeOffset.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public void MonitoringRuntimeEventSnapshot_CanCarryMixedValidityEntries()
    {
        var events = new List<MonitoringRuntimeEvent>
        {
            MonitoringRuntimeEvent.FromRuntimeEvent(
                new RuntimeEvent { EventId = "e1", RunId = "r1", EventType = RuntimeEventTypes.Accepted },
                index: 0,
                rawLine: "{\"eventType\":\"runtime.accepted\"}"),
            MonitoringRuntimeEvent.FromMalformedLine(1, "{bad", "parse error"),
        };

        var stream = new MonitoringRuntimeEventSnapshot
        {
            RunId = "r1",
            Events = events,
            Cap = 50,
            Truncated = true,
            TotalAvailable = 73,
        };

        Assert.Equal("r1", stream.RunId);
        Assert.Equal(2, stream.Events.Count);
        Assert.Equal(RuntimeEventParseStatus.Valid, stream.Events[0].Status);
        Assert.Equal(RuntimeEventParseStatus.Malformed, stream.Events[1].Status);
        Assert.Equal(50, stream.Cap);
        Assert.True(stream.Truncated);
        Assert.Equal(73, stream.TotalAvailable);
    }

    [Fact]
    public void MonitoringRuntimeEventSnapshot_EmptyStream_RepresentsNoEventFile()
    {
        // When no event stream exists for a run, producers return an empty,
        // non-null stream so clients never have to null-check the feed.
        var stream = new MonitoringRuntimeEventSnapshot
        {
            RunId = "r_missing",
            Events = [],
        };

        Assert.Empty(stream.Events);
        Assert.False(stream.Truncated);
        Assert.Null(stream.TotalAvailable);
    }
}

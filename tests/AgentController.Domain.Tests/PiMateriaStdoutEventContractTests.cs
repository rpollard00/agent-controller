namespace AgentController.Domain.Tests;

/// <summary>
/// Conformance tests for the pi-materia stdout event contract schema.
///
/// These tests assert the internal consistency of the single-source contract
/// defined in <see cref="PiMateriaStdoutEventContract"/> and
/// <see cref="PiMateriaStdoutEventTypes"/>. They catch contract drift at
/// compile/test time rather than at runtime.
/// </summary>
public class PiMateriaStdoutEventContractTests
{
    // ── Schema structural integrity ──────────────────────────────────

    [Fact]
    public void ContractSchema_HasExpectedEntryCount()
    {
        // The schema should define exactly 7 event types.
        // If this assertion fails, a new type was added without updating
        // the expected count (or vice versa).
        Assert.Equal(7, PiMateriaStdoutEventContract.Schema.Count);
    }

    [Fact]
    public void ContractSchema_NoDuplicateTypeStrings()
    {
        var types = PiMateriaStdoutEventContract.Schema.Select(e => e.Type).ToList();
        var distinct = types.Distinct().ToList();
        Assert.Equal(types.Count, distinct.Count);
    }

    [Fact]
    public void ContractSchema_EveryEntryHasRequiredTypeField()
    {
        foreach (var entry in PiMateriaStdoutEventContract.Schema)
        {
            var typeField = entry.Fields.FirstOrDefault(f => f.Name == "type" && f.Required);
            Assert.NotNull(typeField);
            Assert.Equal("string", typeField.Type);
        }
    }

    [Fact]
    public void ContractSchema_EveryEntryHasCategory()
    {
        var validCategories = new[] { "rpc", "cast", "agent", "materia" };
        foreach (var entry in PiMateriaStdoutEventContract.Schema)
        {
            Assert.Contains(entry.Category, validCategories, StringComparer.Ordinal);
        }
    }

    [Fact]
    public void ContractSchema_EveryEntryHasNonEmptyFields()
    {
        foreach (var entry in PiMateriaStdoutEventContract.Schema)
        {
            Assert.NotEmpty(entry.Fields);
        }
    }

    [Fact]
    public void ContractSchema_EveryEntryHasDescription()
    {
        foreach (var entry in PiMateriaStdoutEventContract.Schema)
        {
            Assert.NotNull(entry.Description);
            Assert.NotEmpty(entry.Description);
        }
    }

    // ── Version consistency ──────────────────────────────────────────

    [Fact]
    public void ContractVersion_IsGreaterThanZero()
    {
        Assert.True(PiMateriaStdoutEventTypes.ContractVersion > 0);
        Assert.True(PiMateriaStdoutEventContract.ContractVersion > 0);
    }

    [Fact]
    public void ContractVersion_MatchesBetweenConstantsAndSchema()
    {
        Assert.Equal(
            PiMateriaStdoutEventTypes.ContractVersion,
            PiMateriaStdoutEventContract.ContractVersion
        );
    }

    // ── Type set partitioning ────────────────────────────────────────

    [Fact]
    public void AllRecognizedTypes_ContainsAllConstantTypes()
    {
        // Every constant string must be in the AllRecognizedTypes set.
        var constants = new[]
        {
            PiMateriaStdoutEventTypes.Response,
            PiMateriaStdoutEventTypes.ExtensionError,
            PiMateriaStdoutEventTypes.CastStart,
            PiMateriaStdoutEventTypes.CastEnd,
            PiMateriaStdoutEventTypes.AgentEnd,
            PiMateriaStdoutEventTypes.MateriaStart,
            PiMateriaStdoutEventTypes.MateriaEnd,
        };

        foreach (var constant in constants)
        {
            Assert.True(
                PiMateriaStdoutEventTypes.AllRecognizedTypes.Contains(constant),
                $"Constant '{constant}' is not in AllRecognizedTypes"
            );
        }
    }

    [Fact]
    public void AllRecognizedTypes_CountMatchesConstantCount()
    {
        // The set should have exactly 7 entries — one for each constant.
        Assert.Equal(7, PiMateriaStdoutEventTypes.AllRecognizedTypes.Count);
    }

    [Fact]
    public void TerminalAndIntermediate_PartitionAllRecognizedTypes()
    {
        var all = PiMateriaStdoutEventTypes.AllRecognizedTypes.ToList();
        var terminal = PiMateriaStdoutEventTypes.TerminalTypes.ToList();
        var intermediate = PiMateriaStdoutEventTypes.IntermediateTypes.ToList();

        // No overlap
        var overlap = terminal.Intersect(intermediate).ToList();
        Assert.Empty(overlap);

        // Union equals all
        var union = terminal.Union(intermediate).ToList();
        Assert.Equal(all.Count, union.Count);
        Assert.All(all, t => Assert.Contains(t, union));
    }

    [Fact]
    public void TerminalTypes_ContainsCastEnd()
    {
        // cast_end is the whole-cast terminal signal — unlike agent_end which
        // is per-socket and non-terminal. The runtime treats cast_end as a strong
        // terminal signal that confirms whole-cast completion.
        Assert.Contains(
            PiMateriaStdoutEventTypes.CastEnd,
            PiMateriaStdoutEventTypes.TerminalTypes
        );
    }

    [Fact]
    public void TerminalTypes_DoesNotContainAgentEnd()
    {
        // agent_end is per-socket and non-terminal — it fires once per socket
        // in multi-socket casts, so the runtime must stay alive for subsequent sockets.
        Assert.DoesNotContain(
            PiMateriaStdoutEventTypes.AgentEnd,
            PiMateriaStdoutEventTypes.TerminalTypes
        );
    }

    [Fact]
    public void IntermediateTypes_ContainsAgentEnd()
    {
        // agent_end is per-socket and non-terminal — it fires once per socket
        // in multi-socket casts, so the runtime must stay alive for subsequent sockets.
        Assert.Contains(
            PiMateriaStdoutEventTypes.AgentEnd,
            PiMateriaStdoutEventTypes.IntermediateTypes
        );
    }

    [Fact]
    public void IntermediateTypes_DoesNotContainCastEnd()
    {
        // cast_end is terminal, not intermediate — it signals whole-cast completion.
        Assert.DoesNotContain(
            PiMateriaStdoutEventTypes.CastEnd,
            PiMateriaStdoutEventTypes.IntermediateTypes
        );
    }

    // ── Schema-to-set cross-reference ────────────────────────────────

    [Fact]
    public void SchemaTypes_MatchAllRecognizedTypes()
    {
        var schemaTypes = PiMateriaStdoutEventContract.Schema.Select(e => e.Type).ToHashSet();
        var recognizedTypes = PiMateriaStdoutEventTypes.AllRecognizedTypes.ToHashSet();

        var missingFromRecognized = schemaTypes.Except(recognizedTypes).ToList();
        Assert.Empty(missingFromRecognized);

        var missingFromSchema = recognizedTypes.Except(schemaTypes).ToList();
        Assert.Empty(missingFromSchema);
    }

    [Fact]
    public void SchemaTerminalClassification_MatchesTerminalTypesSet()
    {
        var schemaTerminal = PiMateriaStdoutEventContract.TerminalTypeStrings;
        var setTerminal = PiMateriaStdoutEventTypes.TerminalTypes.ToList();

        Assert.Equal(setTerminal, schemaTerminal);
    }

    [Fact]
    public void SchemaIntermediateClassification_MatchesIntermediateTypesSet()
    {
        var schemaIntermediate = PiMateriaStdoutEventContract.IntermediateTypeStrings;
        var setIntermediate = PiMateriaStdoutEventTypes.IntermediateTypes.ToList();

        Assert.Equal(setIntermediate, schemaIntermediate);
    }

    // ── Schema lookup ────────────────────────────────────────────────

    [Fact]
    public void GetEntry_ReturnsEntryForKnownType()
    {
        var entry = PiMateriaStdoutEventContract.GetEntry(PiMateriaStdoutEventTypes.AgentEnd);
        Assert.NotNull(entry);
        Assert.False(entry.IsTerminal);
        Assert.Equal("agent", entry.Category);
    }

    [Fact]
    public void GetEntry_CastEndIsTerminal()
    {
        // cast_end is the whole-cast terminal signal — unlike agent_end.
        var entry = PiMateriaStdoutEventContract.GetEntry(PiMateriaStdoutEventTypes.CastEnd);
        Assert.NotNull(entry);
        Assert.True(entry.IsTerminal);
        Assert.Equal("cast", entry.Category);
    }

    [Fact]
    public void GetEntry_ReturnsNullForUnknownType()
    {
        var entry = PiMateriaStdoutEventContract.GetEntry("__unknown_event__");
        Assert.Null(entry);
    }

    [Fact]
    public void GetEntry_ReturnsEntryForEveryRecognizedType()
    {
        foreach (var type in PiMateriaStdoutEventTypes.AllRecognizedTypes)
        {
            var entry = PiMateriaStdoutEventContract.GetEntry(type);
            Assert.NotNull(entry);
            Assert.Equal(type, entry.Type);
        }
    }

    // ── Built-in validation ──────────────────────────────────────────

    [Fact]
    public void Validate_PassesWithoutError()
    {
        // The Validate() method performs all structural checks in one call.
        // If any invariant is violated, it throws InvalidOperationException.
        PiMateriaStdoutEventContract.Validate();
    }

    // ── Field-level schema assertions ────────────────────────────────

    [Fact]
    public void ResponseEvent_HasExpectedFields()
    {
        var entry = PiMateriaStdoutEventContract.GetEntry(PiMateriaStdoutEventTypes.Response)!;
        var fieldNames = entry.Fields.Select(f => f.Name).ToList();

        Assert.Contains("type", fieldNames);
        Assert.Contains("command", fieldNames);
        Assert.Contains("success", fieldNames);
        Assert.Contains("error", fieldNames);

        // command and success are required; error is optional.
        Assert.True(entry.Fields.First(f => f.Name == "command").Required);
        Assert.True(entry.Fields.First(f => f.Name == "success").Required);
        Assert.False(entry.Fields.First(f => f.Name == "error").Required);
    }

    [Fact]
    public void CastStartEvent_HasSocketsField()
    {
        var entry = PiMateriaStdoutEventContract.GetEntry(PiMateriaStdoutEventTypes.CastStart)!;
        var fieldNames = entry.Fields.Select(f => f.Name).ToList();

        Assert.Contains("type", fieldNames);
        Assert.Contains("castId", fieldNames);
        Assert.Contains("eventing", fieldNames);
        Assert.Contains("sockets", fieldNames);

        // sockets is required and is an array.
        var socketsField = entry.Fields.First(f => f.Name == "sockets");
        Assert.True(socketsField.Required);
        Assert.Equal("array", socketsField.Type);
    }

    [Fact]
    public void AgentEndEvent_IsNonTerminal()
    {
        var entry = PiMateriaStdoutEventContract.GetEntry(PiMateriaStdoutEventTypes.AgentEnd)!;
        Assert.False(entry.IsTerminal);
        Assert.Equal("agent", entry.Category);
    }

    [Fact]
    public void MateriaStartEvent_HasMateriaNameField()
    {
        var entry = PiMateriaStdoutEventContract.GetEntry(PiMateriaStdoutEventTypes.MateriaStart)!;
        var fieldNames = entry.Fields.Select(f => f.Name).ToList();

        Assert.Contains("materiaName", fieldNames);
        Assert.Contains("socketName", fieldNames);

        var materiaNameField = entry.Fields.First(f => f.Name == "materiaName");
        Assert.True(materiaNameField.Required);
        Assert.Equal("string", materiaNameField.Type);
    }

    // ── Telemetry ignore list ────────────────────────────────────────

    [Fact]
    public void TelemetryIgnoreList_ContainsExpectedTelemetryTypes()
    {
        // Every known telemetry-only type must be in the ignore list.
        var expectedTelemetryTypes = new[]
        {
            PiMateriaStdoutEventTypes.TelemetryExtensionUiRequest,
            PiMateriaStdoutEventTypes.TelemetrySessionInfoChanged,
            PiMateriaStdoutEventTypes.TelemetryMessageStart,
            PiMateriaStdoutEventTypes.TelemetryMessageEnd,
            PiMateriaStdoutEventTypes.TelemetryMessageUpdate,
            PiMateriaStdoutEventTypes.TelemetryAgentStart,
            PiMateriaStdoutEventTypes.TelemetryTurnStart,
            PiMateriaStdoutEventTypes.TelemetryTurnEnd,
            PiMateriaStdoutEventTypes.TelemetryToolExecutionStart,
            PiMateriaStdoutEventTypes.TelemetryToolExecutionEnd,
        };

        foreach (var type in expectedTelemetryTypes)
        {
            Assert.True(
                PiMateriaStdoutEventTypes.TelemetryIgnoreList.Contains(type),
                $"Telemetry type '{type}' is not in TelemetryIgnoreList"
            );
        }
    }

    [Fact]
    public void TelemetryIgnoreList_DoesNotContainLifecycleEvents()
    {
        // The ignore list must NOT swallow real lifecycle events.
        var lifecycleTypes = PiMateriaStdoutEventTypes.AllRecognizedTypes.ToList();
        var ignoreList = PiMateriaStdoutEventTypes.TelemetryIgnoreList.ToList();

        var overlap = lifecycleTypes.Intersect(ignoreList).ToList();
        Assert.True(
            overlap.Count == 0,
            $"TelemetryIgnoreList contains lifecycle event types: {string.Join(", ", overlap)}"
        );
    }

    [Fact]
    public void TelemetryIgnoreList_CountMatchesConstantCount()
    {
        // The ignore list should have exactly 10 entries — one for each telemetry constant.
        Assert.Equal(10, PiMateriaStdoutEventTypes.TelemetryIgnoreList.Count);
    }

    [Fact]
    public void TelemetryConstants_HaveExpectedStringValues()
    {
        // Assert the actual string values match what pi-core emits.
        Assert.Equal("extension_ui_request", PiMateriaStdoutEventTypes.TelemetryExtensionUiRequest);
        Assert.Equal("session_info_changed", PiMateriaStdoutEventTypes.TelemetrySessionInfoChanged);
        Assert.Equal("message_start", PiMateriaStdoutEventTypes.TelemetryMessageStart);
        Assert.Equal("message_end", PiMateriaStdoutEventTypes.TelemetryMessageEnd);
        Assert.Equal("message_update", PiMateriaStdoutEventTypes.TelemetryMessageUpdate);
        Assert.Equal("agent_start", PiMateriaStdoutEventTypes.TelemetryAgentStart);
        Assert.Equal("turn_start", PiMateriaStdoutEventTypes.TelemetryTurnStart);
        Assert.Equal("turn_end", PiMateriaStdoutEventTypes.TelemetryTurnEnd);
        Assert.Equal("tool_execution_start", PiMateriaStdoutEventTypes.TelemetryToolExecutionStart);
        Assert.Equal("tool_execution_end", PiMateriaStdoutEventTypes.TelemetryToolExecutionEnd);
    }
}

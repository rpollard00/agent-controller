namespace AgentController.Domain;

/// <summary>
/// Canonical event-type constants for the pi-materia stdout RPC protocol.
///
/// These are the event <c>type</c> strings that pi emits on its stdout JSONL
/// stream when driven in RPC mode (<c>pi --mode rpc</c>). The
/// <see cref="PiMateriaRuntime"/> parser must recognize every type listed here;
/// any type not in this set is treated as contract drift.
///
/// This is the single-source definition of the stdout event contract. Both the
/// pi-materia emitter (TypeScript side) and the agent-controller parser (C# side)
/// reference these constants to prevent drift.
///
/// <para><b>Contract version:</b> <see cref="ContractVersion" /> — see <see cref="PiMateriaStdoutEventContract"/> for the full schema.</para>
/// </summary>
public static class PiMateriaStdoutEventTypes
{
    // ── Contract metadata ───────────────────────────────────────────

    /// <summary>
    /// Schema version for the pi-materia stdout event contract.
    /// Increment this when new types are added or semantics change.
    /// </summary>
    public const int ContractVersion = 1;

    // ── RPC protocol events ─────────────────────────────────────────

    /// <summary>
    /// RPC command response — signals that an RPC command (e.g. <c>prompt</c>)
    /// was accepted or rejected. Contains <c>command</c>, <c>success</c>, and
    /// optionally <c>error</c> fields.
    /// </summary>
    /// <remarks>Intermediate — does not drive lifecycle state transitions.</remarks>
    public const string Response = "response";

    /// <summary>
    /// pi-materia extension error — a materialization error from a pi extension.
    /// Contains an <c>error</c> field with the error message.
    /// </summary>
    /// <remarks>Intermediate — logged as warning but does not terminate the run.</remarks>
    public const string ExtensionError = "extension_error";

    // ── Cast lifecycle events ───────────────────────────────────────

    /// <summary>
    /// pi-materia cast initialization event. Contains resolved socket metadata
    /// (<c>sockets</c> array with <c>socketName</c>, <c>type</c>, <c>materiaName</c>,
    /// <c>multiTurn</c>), the <c>castId</c>, and the active <c>eventing</c> preset.
    /// </summary>
    /// <remarks>
    /// Intermediate — the runtime inspects this for multiTurn agent sockets
    /// under agent-controller eventing and may fail the cast fast. The full
    /// socket metadata is persisted as a lifecycle artifact for diagnosability.
    /// </remarks>
    public const string CastStart = "cast_start";

    /// <summary>
    /// pi-materia cast completion event. Emitted when all sockets in the
    /// pipeline have completed and the cast is done.
    /// </summary>
    /// <remarks>
    /// Intermediate — informational. The runtime does not derive lifecycle
    /// state from this; the webhook (<c>runtime.completed</c>) is authoritative.
    /// </remarks>
    public const string CastEnd = "cast_end";

    // ── Agent lifecycle events ──────────────────────────────────────

    /// <summary>
    /// pi-materia agent socket completed its turn. Emitted when an agent
    /// socket finishes its single-turn work. Under agent-controller eventing
    /// this signals the cast is done (the controller never sends
    /// <c>/materia continue</c>), so the runtime initiates graceful shutdown.
    /// </summary>
    /// <remarks>
    /// Terminal for single-turn agent sockets under agent-controller eventing.
    /// The runtime recognizes this and shuts down the pi process so the run
    /// does not stall waiting for a <c>/materia continue</c> that never comes.
    /// </remarks>
    public const string AgentEnd = "agent_end";

    // ── Materia lifecycle events ────────────────────────────────────

    /// <summary>
    /// pi-materia materia socket started execution. Contains the materia name
    /// and socket identifier.
    /// </summary>
    /// <remarks>Intermediate — informational, logged for traceability.</remarks>
    public const string MateriaStart = "materia_start";

    /// <summary>
    /// pi-materia materia socket completed execution. Contains the materia name
    /// and socket identifier.
    /// </summary>
    /// <remarks>Intermediate — informational, logged for traceability.</remarks>
    public const string MateriaEnd = "materia_end";

    // ── Complete set of recognized types ────────────────────────────

    /// <summary>
    /// Returns the complete set of recognized pi-materia stdout event type strings.
    /// Used by the runtime parser to validate incoming events and by conformance
    /// tests to assert no drift between the contract and the implementation.
    /// </summary>
    public static IReadOnlySet<string> AllRecognizedTypes { get; } = new HashSet<string>
    {
        Response,
        ExtensionError,
        CastStart,
        CastEnd,
        AgentEnd,
        MateriaStart,
        MateriaEnd,
    };

    /// <summary>
    /// Returns the set of terminal event types that signal the agent's work
    /// is complete and the runtime should initiate shutdown.
    /// </summary>
    /// <remarks>
    /// Under agent-controller eventing, <c>agent_end</c> is the primary terminal
    /// indicator for single-turn agent sockets. The webhook (<c>runtime.completed</c>)
    /// is the authoritative terminal signal; stdout terminal types are used to
    /// prevent stalls when the webhook is delayed.
    /// </remarks>
    public static IReadOnlySet<string> TerminalTypes { get; } = new HashSet<string>
    {
        AgentEnd,
    };

    /// <summary>
    /// Returns the set of intermediate (non-terminal) event types.
    /// These are recognized and processed but do not signal completion.
    /// </summary>
    public static IReadOnlySet<string> IntermediateTypes { get; } = new HashSet<string>
    {
        Response,
        ExtensionError,
        CastStart,
        CastEnd,
        MateriaStart,
        MateriaEnd,
    };
}

// ── Single-source event-type contract schema ─────────────────────────

/// <summary>
/// Field descriptor for a single field in a pi-materia stdout event.
/// </summary>
/// <param name="Name">The JSON field name as emitted by pi.</param>
/// <param name="Type">The JSON value type (e.g. "string", "boolean", "object", "array").</param>
/// <param name="Required">Whether the field must be present on every event of this type.</param>
/// <param name="Description">Human-readable description of the field's purpose.</param>
public sealed record StdoutEventField(
    string Name,
    string Type,
    bool Required,
    string Description
);

/// <summary>
/// Schema entry for a single pi-materia stdout event type.
/// Defines the type string, its lifecycle classification, and its field contract.
/// </summary>
/// <param name="Type">The canonical <c>type</c> string emitted on stdout.</param>
/// <param name="Category">Logical grouping (e.g. "rpc", "cast", "agent", "materia").</param>
/// <param name="IsTerminal">Whether this event type signals completion of the agent's work.</param>
/// <param name="Description">Human-readable description of the event's purpose.</param>
/// <param name="Fields">The fields the event carries, with types and required flags.</param>
public sealed record StdoutEventSchemaEntry(
    string Type,
    string Category,
    bool IsTerminal,
    string Description,
    IReadOnlyList<StdoutEventField> Fields
);

/// <summary>
/// Single-source authoritative definition of the pi-materia stdout event contract.
///
/// This class defines every event type the pi-materia emitter can produce on its
/// stdout JSONL stream, including each type's fields, required/optional flags,
/// and terminal vs intermediate classification.
///
/// Both the pi-materia emitter (TypeScript side) and the agent-controller
/// <see cref="PiMateriaRuntime"/> parser (C# side) reference this schema to
/// prevent contract drift. The conformance tests in
/// <c>PiMateriaStdoutEventContractTests</c> assert that:
/// <list type="bullet">
///   <item>Every type in the schema is in <see cref="PiMateriaStdoutEventTypes.AllRecognizedTypes"/>.</item>
///   <item>Every type in <see cref="PiMateriaStdoutEventTypes.AllRecognizedTypes"/> has a schema entry.</item>
///   <item>Terminal and intermediate sets partition the full set with no overlap or gaps.</item>
///   <item>No duplicate type strings exist across the schema entries.</item>
/// </list>
///
/// <para><b>Contract version:</b> <see cref="ContractVersion"/> — increment when new types
/// are added, fields change, or semantics are modified.</para>
/// </summary>
public static class PiMateriaStdoutEventContract
{
    /// <summary>
    /// Schema version for the pi-materia stdout event contract.
    /// Increment this when new types are added or semantics change.
    /// </summary>
    /// <remarks>
    /// This mirrors <see cref="PiMateriaStdoutEventTypes.ContractVersion"/>.
    /// The conformance test asserts they are equal.
    /// </remarks>
    public const int ContractVersion = 1;

    /// <summary>
    /// The complete schema of recognized pi-materia stdout event types.
    /// This is the authoritative list — both emitter and parser derive from it.
    /// </summary>
    public static IReadOnlyList<StdoutEventSchemaEntry> Schema { get; } = new[]
    {
        // ── RPC protocol events ─────────────────────────────────────────

        new StdoutEventSchemaEntry(
            Type: PiMateriaStdoutEventTypes.Response,
            Category: "rpc",
            IsTerminal: false,
            Description: "RPC command response — signals that an RPC command (e.g. prompt) was accepted or rejected.",
            Fields: new[]
            {
                new StdoutEventField("type", "string", true, "Always 'response'"),
                new StdoutEventField("command", "string", true, "The RPC command this response corresponds to (e.g. 'prompt', 'abort')"),
                new StdoutEventField("success", "boolean", true, "Whether the command was accepted"),
                new StdoutEventField("error", "string", false, "Error message if success is false"),
            }),

        new StdoutEventSchemaEntry(
            Type: PiMateriaStdoutEventTypes.ExtensionError,
            Category: "rpc",
            IsTerminal: false,
            Description: "pi-materia extension error — a materialization error from a pi extension.",
            Fields: new[]
            {
                new StdoutEventField("type", "string", true, "Always 'extension_error'"),
                new StdoutEventField("error", "string", true, "The error message from the extension"),
            }),

        // ── Cast lifecycle events ───────────────────────────────────────

        new StdoutEventSchemaEntry(
            Type: PiMateriaStdoutEventTypes.CastStart,
            Category: "cast",
            IsTerminal: false,
            Description: "pi-materia cast initialization event. Contains resolved socket metadata, castId, and the active eventing preset.",
            Fields: new[]
            {
                new StdoutEventField("type", "string", true, "Always 'cast_start'"),
                new StdoutEventField("castId", "string", true, "Unique identifier for this cast"),
                new StdoutEventField("eventing", "object", true, "Eventing configuration with 'preset' field (e.g. 'agent-controller', 'interactive')"),
                new StdoutEventField("sockets", "array", true, "Array of resolved socket objects, each with socketName, type, materiaName, multiTurn"),
                new StdoutEventField("loadout", "string", false, "Name of the active loadout"),
                new StdoutEventField("loadoutId", "string", false, "Unique identifier of the active loadout"),
            }),

        new StdoutEventSchemaEntry(
            Type: PiMateriaStdoutEventTypes.CastEnd,
            Category: "cast",
            IsTerminal: false,
            Description: "pi-materia cast completion event. Emitted when all sockets in the pipeline have completed.",
            Fields: new[]
            {
                new StdoutEventField("type", "string", true, "Always 'cast_end'"),
                new StdoutEventField("castId", "string", true, "The castId from the corresponding cast_start event"),
            }),

        // ── Agent lifecycle events ──────────────────────────────────────

        new StdoutEventSchemaEntry(
            Type: PiMateriaStdoutEventTypes.AgentEnd,
            Category: "agent",
            IsTerminal: true,
            Description: "pi-materia agent socket completed its turn. Under agent-controller eventing this signals the cast is done and the runtime should initiate graceful shutdown.",
            Fields: new[]
            {
                new StdoutEventField("type", "string", true, "Always 'agent_end'"),
                new StdoutEventField("messages", "array", true, "Array of message objects produced during the agent turn"),
            }),

        // ── Materia lifecycle events ────────────────────────────────────

        new StdoutEventSchemaEntry(
            Type: PiMateriaStdoutEventTypes.MateriaStart,
            Category: "materia",
            IsTerminal: false,
            Description: "pi-materia materia socket started execution. Contains the materia name and socket identifier.",
            Fields: new[]
            {
                new StdoutEventField("type", "string", true, "Always 'materia_start'"),
                new StdoutEventField("materiaName", "string", true, "Name of the materia being executed"),
                new StdoutEventField("socketName", "string", true, "Name of the socket this materia is bound to"),
            }),

        new StdoutEventSchemaEntry(
            Type: PiMateriaStdoutEventTypes.MateriaEnd,
            Category: "materia",
            IsTerminal: false,
            Description: "pi-materia materia socket completed execution. Contains the materia name and socket identifier.",
            Fields: new[]
            {
                new StdoutEventField("type", "string", true, "Always 'materia_end'"),
                new StdoutEventField("materiaName", "string", true, "Name of the materia that completed"),
                new StdoutEventField("socketName", "string", true, "Name of the socket this materia was bound to"),
            }),
    };

    /// <summary>
    /// Returns all terminal event type strings from the schema.
    /// </summary>
    public static IReadOnlyList<string> TerminalTypeStrings =>
        Schema.Where(e => e.IsTerminal).Select(e => e.Type).ToList();

    /// <summary>
    /// Returns all intermediate (non-terminal) event type strings from the schema.
    /// </summary>
    public static IReadOnlyList<string> IntermediateTypeStrings =>
        Schema.Where(e => !e.IsTerminal).Select(e => e.Type).ToList();

    /// <summary>
    /// Looks up a schema entry by its type string. Returns null if not found.
    /// </summary>
    public static StdoutEventSchemaEntry? GetEntry(string type)
    {
        return Schema.FirstOrDefault(e => e.Type == type);
    }

    /// <summary>
    /// Validates the internal consistency of the contract schema.
    /// Throws <see cref="InvalidOperationException"/> if any invariant is violated.
    /// </summary>
    public static void Validate()
    {
        var schemaTypes = Schema.Select(e => e.Type).ToList();
        var recognizedTypes = PiMateriaStdoutEventTypes.AllRecognizedTypes.ToList();
        var terminalTypes = PiMateriaStdoutEventTypes.TerminalTypes.ToList();
        var intermediateTypes = PiMateriaStdoutEventTypes.IntermediateTypes.ToList();

        // 1. Schema version consistency
        if (ContractVersion != PiMateriaStdoutEventTypes.ContractVersion)
        {
            throw new InvalidOperationException(
                $"Contract version mismatch: PiMateriaStdoutEventContract.ContractVersion={ContractVersion} " +
                $"!= PiMateriaStdoutEventTypes.ContractVersion={PiMateriaStdoutEventTypes.ContractVersion}"
            );
        }

        // 2. No duplicate type strings in schema
        var duplicates = schemaTypes.GroupBy(t => t).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (duplicates.Count > 0)
        {
            throw new InvalidOperationException(
                $"Duplicate type strings in schema: {string.Join(", ", duplicates)}"
            );
        }

        // 3. Every schema type is in AllRecognizedTypes
        var missingFromRecognized = schemaTypes.Where(t => !recognizedTypes.Contains(t)).ToList();
        if (missingFromRecognized.Count > 0)
        {
            throw new InvalidOperationException(
                $"Schema types not in AllRecognizedTypes: {string.Join(", ", missingFromRecognized)}"
            );
        }

        // 4. Every recognized type has a schema entry
        var missingFromSchema = recognizedTypes.Where(t => !schemaTypes.Contains(t)).ToList();
        if (missingFromSchema.Count > 0)
        {
            throw new InvalidOperationException(
                $"AllRecognizedTypes not in schema: {string.Join(", ", missingFromSchema)}"
            );
        }

        // 5. Terminal and intermediate sets partition AllRecognizedTypes
        var terminalSet = new HashSet<string>(terminalTypes);
        var intermediateSet = new HashSet<string>(intermediateTypes);
        var allSet = new HashSet<string>(recognizedTypes);

        var overlap = terminalSet.Intersect(intermediateSet).ToList();
        if (overlap.Count > 0)
        {
            throw new InvalidOperationException(
                $"Types in both TerminalTypes and IntermediateTypes: {string.Join(", ", overlap)}"
            );
        }

        var union = terminalSet.Union(intermediateSet).ToList();
        var gaps = allSet.Except(union).ToList();
        if (gaps.Count > 0)
        {
            throw new InvalidOperationException(
                $"Types in AllRecognizedTypes but not in TerminalTypes or IntermediateTypes: {string.Join(", ", gaps)}"
            );
        }

        var extras = union.Except(allSet).ToList();
        if (extras.Count > 0)
        {
            throw new InvalidOperationException(
                $"Types in TerminalTypes or IntermediateTypes but not in AllRecognizedTypes: {string.Join(", ", extras)}"
            );
        }

        // 6. Schema terminal/intermediate classification matches the sets
        var schemaTerminal = TerminalTypeStrings;
        var schemaIntermediate = IntermediateTypeStrings;

        var terminalMismatch = schemaTerminal.Except(terminalTypes).Concat(terminalTypes.Except(schemaTerminal)).ToList();
        if (terminalMismatch.Count > 0)
        {
            throw new InvalidOperationException(
                $"Terminal type mismatch between schema and TerminalTypes set: {string.Join(", ", terminalMismatch)}"
            );
        }

        var intermediateMismatch = schemaIntermediate.Except(intermediateTypes).Concat(intermediateTypes.Except(schemaIntermediate)).ToList();
        if (intermediateMismatch.Count > 0)
        {
            throw new InvalidOperationException(
                $"Intermediate type mismatch between schema and IntermediateTypes set: {string.Join(", ", intermediateMismatch)}"
            );
        }

        // 7. Every schema entry has at least one required field named "type"
        foreach (var entry in Schema)
        {
            var typeField = entry.Fields.FirstOrDefault(f => f.Name == "type" && f.Required);
            if (typeField is null)
            {
                throw new InvalidOperationException(
                    $"Schema entry '{entry.Type}' is missing a required 'type' field"
                );
            }
        }
    }
}

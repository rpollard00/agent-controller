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
/// <para><b>Contract version:</b> 1 — initial alignment after agent_end stall incident.</para>
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

namespace AgentController.Domain;

/// <summary>
/// Discriminates how an agent run should be dispatched to a pi-materia loadout.
/// <see cref="NewWork"/> creates a new PR; <see cref="Rework"/> addresses feedback
/// on an existing branch without opening a new PR.
/// </summary>
public enum ExecutionKind
{
    /// <summary>New work — the runtime should create a new branch and PR.</summary>
    NewWork = 0,

    /// <summary>Rework — the runtime should apply fixes to an existing branch.</summary>
    Rework,
}

namespace AgentController.Infrastructure;

/// <summary>
/// Declarative set of terminal board states that are excluded from new-work discovery.
///
/// These states represent work items that have reached a final disposition and should
/// not be polled for new work. Matched via case-insensitive exact comparison against
/// Azure DevOps board item <c>State</c> values.
///
/// This is the single source of truth for terminal-state filtering. It is not
/// configurable per environment. Rework reactivation (e.g., overriding
/// rework-requested tags on pull requests) still takes precedence over this filter.
/// </summary>
public static class BoardTerminalStates
{
    /// <summary>
    /// The fixed set of terminal states: Closed, Removed, Resolved, Completed.
    /// </summary>
    public static readonly IReadOnlyList<string> Values =
        new[] { "Closed", "Removed", "Resolved", "Completed" };
}

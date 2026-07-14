namespace AgentController.Application.Abstractions;

/// <summary>
/// Minimal abstraction over work-source configuration options.
/// Allows the Application layer to read organization/project settings
/// without depending on the Infrastructure options types directly.
/// </summary>
public interface IWorkSourceOptions
{
    /// <summary>Azure DevOps organization URL, or <c>null</c> if not configured.</summary>
    string? OrganizationUrl { get; }

    /// <summary>Azure DevOps project name, or <c>null</c> if not configured.</summary>
    string? Project { get; }

    /// <summary>
    /// State to set on a work item when the controller starts working on it
    /// (e.g. "Active"). Used for lifecycle projection to the external board.
    /// When <c>null</c>, no state change is made on claim/active transitions.
    /// </summary>
    string? ActiveState { get; }

    /// <summary>
    /// State to set on a work item when the controller completes it
    /// (e.g. "Resolved"). Used for lifecycle projection to the external board.
    /// When <c>null</c>, no state change is made on completion.
    /// </summary>
    string? CompletedState { get; }

    /// <summary>
    /// Work item states that are eligible for autonomous pickup.
    /// Used to determine the target state when releasing a claim
    /// so the work item becomes immediately retryable.
    /// </summary>
    IReadOnlyList<string>? EligibleStates { get; }

    /// <summary>
    /// Work item states that are considered finished and not picked up.
    /// Items in these states are excluded from discovery queries.
    /// </summary>
    IReadOnlyList<string> CompletedStates { get; }

    /// <summary>
    /// Prefix used for controller-owned lifecycle tags on the board.
    /// Defaults to "agent" when not configured.
    /// </summary>
    string TagPrefix { get; }
}

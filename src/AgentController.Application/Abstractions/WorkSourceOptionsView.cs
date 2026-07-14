namespace AgentController.Application.Abstractions;

/// <summary>
/// Concrete options class implementing <see cref="IWorkSourceOptions"/>.
/// Used by the Application layer to read work-source configuration
/// (organization URL, project, active/completed states) without depending
/// on the Infrastructure <c>WorkSourceOptions</c> type directly.
/// </summary>
public sealed class WorkSourceOptionsView : IWorkSourceOptions
{
    /// <inheritdoc />
    public string? OrganizationUrl { get; set; }

    /// <inheritdoc />
    public string? Project { get; set; }

    /// <inheritdoc />
    public string? ActiveState { get; set; }

    /// <inheritdoc />
    public string? CompletedState { get; set; }

    /// <inheritdoc />
    public IReadOnlyList<string> CompletedStates { get; set; } = [];

    /// <inheritdoc />
    public string TagPrefix { get; set; } = "agent";
}

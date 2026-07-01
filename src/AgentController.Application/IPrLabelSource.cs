namespace AgentController.Application;

/// <summary>
/// A single pull-request label.
/// Used by the marker gate to verify the rework marker is present.
/// </summary>
public sealed record PrLabel
{
    /// <summary>Label name (e.g. "agent-rework-requested").</summary>
    public string Name { get; init; } = string.Empty;
}

/// <summary>
/// Port for fetching pull-request labels from the source system.
///
/// Used by the feedback filter pipeline's marker gate to verify that
/// the rework marker label was applied by an allowed reviewer.
/// </summary>
public interface IPrLabelSource
{
    /// <summary>
    /// Fetch the labels for a single pull request.
    /// Returns an empty list when labels cannot be fetched (treated as
    /// "no labels" by the marker gate, which fails-closed).
    /// </summary>
    Task<IReadOnlyList<PrLabel>> GetLabelsAsync(
        PrUnderTest pr,
        CancellationToken cancellationToken);
}

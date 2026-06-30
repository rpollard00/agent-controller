namespace AgentController.Application;

/// <summary>
/// A single pull-request label with the identity of the user who created it.
/// Used by the marker gate to verify the rework marker was applied by an
/// allowed reviewer.
/// </summary>
public sealed record PrLabel
{
    /// <summary>Label name (e.g. "agent-rework-requested").</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Canonical identifier of the user who created the label
    /// (uniqueName / email). Empty string when unavailable.
    /// </summary>
    public string CreatedBy { get; init; } = string.Empty;
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

using AgentController.Domain;

namespace AgentController.Application;

/// <summary>
/// Describes the labels to add to and remove from a pull request.
/// Labels are compared case-insensitively. Removals are applied before additions,
/// so an entry present in both collections remains applied.
/// </summary>
public sealed record PullRequestLabelMutation
{
    /// <summary>Labels that should be present after the mutation.</summary>
    public IReadOnlyList<string> LabelsToAdd { get; init; } = [];

    /// <summary>Labels that should be absent after the mutation.</summary>
    public IReadOnlyList<string> LabelsToRemove { get; init; } = [];
}

/// <summary>
/// Provider-neutral port for idempotently changing labels on an existing pull request.
/// Implementations compare label names case-insensitively and safely tolerate a mutation
/// being repeated after a retry or restart.
/// </summary>
public interface IPullRequestLabelMutator
{
    /// <summary>Applies the requested label additions and removals.</summary>
    Task MutateAsync(
        PullRequestReference pullRequest,
        PullRequestLabelMutation mutation,
        CancellationToken cancellationToken
    );
}

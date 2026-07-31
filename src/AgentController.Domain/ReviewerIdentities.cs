namespace AgentController.Domain;

/// <summary>
/// Provider-neutral reviewer identity used by managed repository profiles and
/// review-comment author aliases.
/// </summary>
public sealed record ReviewerIdentity
{
    /// <summary>Provider-defined identity kind.</summary>
    public string Kind { get; init; } = string.Empty;

    /// <summary>Identity value within <see cref="Kind"/>.</summary>
    public string Value { get; init; } = string.Empty;
}

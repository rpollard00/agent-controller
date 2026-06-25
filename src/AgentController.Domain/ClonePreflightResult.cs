namespace AgentController.Domain;

/// <summary>
/// Result of a source-control clone preflight check.
/// Reports whether the configured clone URL and transport are ready
/// before the worker commits to a claim.
/// </summary>
public sealed record ClonePreflightResult
{
    /// <summary>Whether all preflight checks passed.</summary>
    public bool Success { get; init; }

    /// <summary>
    /// Concrete reason for failure when <see cref="Success"/> is false.
    /// Empty when the preflight passed.
    /// </summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>
    /// The resolved transport type that was checked.
    /// </summary>
    public CloneTransport Transport { get; init; }

    /// <summary>
    /// Normalized clone URL that was checked.
    /// </summary>
    public string CloneUrl { get; init; } = string.Empty;

    /// <summary>
    /// Create a successful preflight result.
    /// </summary>
    public static ClonePreflightResult Ok(CloneTransport transport, string cloneUrl) =>
        new()
        {
            Success = true,
            Reason = string.Empty,
            Transport = transport,
            CloneUrl = cloneUrl,
        };

    /// <summary>
    /// Create a failed preflight result with a concrete reason.
    /// </summary>
    public static ClonePreflightResult Failed(
        CloneTransport transport,
        string cloneUrl,
        string reason) =>
        new()
        {
            Success = false,
            Reason = reason,
            Transport = transport,
            CloneUrl = cloneUrl,
        };
}

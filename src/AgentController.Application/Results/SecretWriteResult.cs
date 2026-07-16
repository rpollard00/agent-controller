namespace AgentController.Application.Results;

/// <summary>
/// Result of a secret write operation from <see cref="ISecretStore.WriteAsync"/>.
/// </summary>
public sealed record SecretWriteResult
{
    private static readonly IReadOnlyList<string> NoErrors = Array.Empty<string>();

    /// <summary>Whether the write operation succeeded.</summary>
    public bool Success { get; init; }

    /// <summary>
    /// Error messages when <see cref="Success"/> is <c>false</c>. Empty on success.
    /// </summary>
    public IReadOnlyList<string> Errors { get; init; } = NoErrors;

    /// <summary>
    /// Optional metadata returned by the store (e.g. version, etag, timestamp).
    /// </summary>
    public string? Metadata { get; init; }

    /// <summary>Create a success result.</summary>
    public static SecretWriteResult SuccessResult(string? metadata = null) =>
        new()
        {
            Success = true,
            Metadata = metadata,
        };

    /// <summary>Create a failure result with one or more error messages.</summary>
    public static SecretWriteResult FailureResult(params string[] errors) =>
        new()
        {
            Success = false,
            Errors = errors.ToList(),
        };
}

namespace AgentController.Application.Results;

/// <summary>
/// Provider-neutral result of a connectivity verification for a repository host connection.
/// </summary>
public sealed record RepositoryHostConnectivityResult
{
    private static readonly IReadOnlyList<string> NoErrors = Array.Empty<string>();

    /// <summary>Whether the connectivity verification succeeded.</summary>
    public bool Success { get; init; }

    /// <summary>
    /// Authentication mechanism used for the verification
    /// (e.g. "PersonalAccessToken").
    /// </summary>
    public string AuthMechanism { get; init; } = string.Empty;

    /// <summary>
    /// HTTP status code from the connectivity check, or <c>null</c> if not applicable.
    /// </summary>
    public int? HttpStatus { get; init; }

    /// <summary>
    /// Error messages when <see cref="Success"/> is <c>false</c>. Empty on success.
    /// </summary>
    public IReadOnlyList<string> Errors { get; init; } = NoErrors;

    /// <summary>
    /// Provider-specific payload data (e.g. enumerated repositories for Azure DevOps).
    /// </summary>
    public object? Payload { get; init; }

    /// <summary>Create a success result.</summary>
    public static RepositoryHostConnectivityResult SuccessResult(
        string authMechanism,
        int? httpStatus = null,
        object? payload = null
    ) =>
        new()
        {
            Success = true,
            AuthMechanism = authMechanism,
            HttpStatus = httpStatus,
            Payload = payload,
        };

    /// <summary>Create a failure result with one or more error messages.</summary>
    public static RepositoryHostConnectivityResult FailureResult(
        IReadOnlyList<string> errors,
        string authMechanism = "",
        int? httpStatus = null,
        object? payload = null
    ) =>
        new()
        {
            Success = false,
            AuthMechanism = authMechanism,
            HttpStatus = httpStatus,
            Errors = errors,
            Payload = payload,
        };
}

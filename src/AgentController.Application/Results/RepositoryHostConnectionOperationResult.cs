using AgentController.Domain;

namespace AgentController.Application.Results;

/// <summary>Identifies the outcome of a managed repository host connection operation.</summary>
public enum RepositoryHostConnectionOperationStatus
{
    /// <summary>The operation completed successfully.</summary>
    Succeeded,

    /// <summary>The supplied connection data was invalid.</summary>
    ValidationFailed,

    /// <summary>The requested connection does not exist.</summary>
    NotFound,

    /// <summary>The operation conflicts with an existing or referencing resource.</summary>
    Conflict,
}

/// <summary>
/// Application-layer result for managed repository host connection operations. Profiles contain
/// only a <see cref="SecretReference"/> for the PAT; credential values are never
/// resolved into this result.
/// </summary>
public sealed record RepositoryHostConnectionOperationResult
{
    private static readonly IReadOnlyDictionary<string, string[]> NoValidationErrors =
        new Dictionary<string, string[]>();

    private RepositoryHostConnectionOperationResult(
        RepositoryHostConnectionOperationStatus status,
        RepositoryHostConnectionProfile? connection = null,
        IReadOnlyDictionary<string, string[]>? validationErrors = null,
        string? detail = null
    )
    {
        Status = status;
        Connection = connection;
        ValidationErrors = validationErrors ?? NoValidationErrors;
        Detail = detail;
    }

    /// <summary>The typed operation outcome.</summary>
    public RepositoryHostConnectionOperationStatus Status { get; }

    /// <summary>The safe managed profile for successful read or mutation operations.</summary>
    public RepositoryHostConnectionProfile? Connection { get; }

    /// <summary>Field-keyed validation errors for invalid input.</summary>
    public IReadOnlyDictionary<string, string[]> ValidationErrors { get; }

    /// <summary>A safe description for not-found or conflict outcomes.</summary>
    public string? Detail { get; }

    public static RepositoryHostConnectionOperationResult Succeeded(
        RepositoryHostConnectionProfile? connection = null
    ) => new(RepositoryHostConnectionOperationStatus.Succeeded, connection);

    public static RepositoryHostConnectionOperationResult ValidationFailed(
        IReadOnlyDictionary<string, string[]> errors
    ) => new(RepositoryHostConnectionOperationStatus.ValidationFailed, validationErrors: errors);

    public static RepositoryHostConnectionOperationResult NotFound(string detail) =>
        new(RepositoryHostConnectionOperationStatus.NotFound, detail: detail);

    public static RepositoryHostConnectionOperationResult Conflict(string detail) =>
        new(RepositoryHostConnectionOperationStatus.Conflict, detail: detail);
}

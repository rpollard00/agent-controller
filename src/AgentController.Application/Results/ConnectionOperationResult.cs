using AgentController.Domain;

namespace AgentController.Application.Results;

/// <summary>Identifies the outcome of a connection profile operation.</summary>
public enum ConnectionOperationStatus
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
/// Application-layer result for connection profile operations.
/// </summary>
public sealed record ConnectionOperationResult
{
    private static readonly IReadOnlyDictionary<string, string[]> NoValidationErrors =
        new Dictionary<string, string[]>();

    private ConnectionOperationResult(
        ConnectionOperationStatus status,
        ConnectionProfile? connection = null,
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
    public ConnectionOperationStatus Status { get; }

    /// <summary>The safe managed profile for successful read or mutation operations.</summary>
    public ConnectionProfile? Connection { get; }

    /// <summary>Field-keyed validation errors for invalid input.</summary>
    public IReadOnlyDictionary<string, string[]> ValidationErrors { get; }

    /// <summary>A safe description for not-found or conflict outcomes.</summary>
    public string? Detail { get; }

    public static ConnectionOperationResult Succeeded(ConnectionProfile? connection = null) =>
        new(ConnectionOperationStatus.Succeeded, connection);

    public static ConnectionOperationResult ValidationFailed(
        IReadOnlyDictionary<string, string[]> errors
    ) => new(ConnectionOperationStatus.ValidationFailed, validationErrors: errors);

    public static ConnectionOperationResult NotFound(string detail) =>
        new(ConnectionOperationStatus.NotFound, detail: detail);

    public static ConnectionOperationResult Conflict(string detail) =>
        new(ConnectionOperationStatus.Conflict, detail: detail);
}

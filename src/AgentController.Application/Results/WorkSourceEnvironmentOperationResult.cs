using AgentController.Domain;

namespace AgentController.Application.Results;

/// <summary>Identifies the outcome of a managed work source environment operation.</summary>
public enum WorkSourceEnvironmentOperationStatus
{
    /// <summary>The operation completed successfully.</summary>
    Succeeded,

    /// <summary>The supplied environment data was invalid.</summary>
    ValidationFailed,

    /// <summary>The requested environment does not exist.</summary>
    NotFound,

    /// <summary>The operation conflicts with an existing or referencing resource.</summary>
    Conflict,
}

/// <summary>
/// Application-layer result for managed work source environment operations. Profiles contain
/// only the name of the environment variable that holds a PAT; credential values are never
/// resolved into this result.
/// </summary>
public sealed record WorkSourceEnvironmentOperationResult
{
    private static readonly IReadOnlyDictionary<string, string[]> NoValidationErrors =
        new Dictionary<string, string[]>();

    private WorkSourceEnvironmentOperationResult(
        WorkSourceEnvironmentOperationStatus status,
        WorkSourceEnvironmentProfile? environment = null,
        IReadOnlyDictionary<string, string[]>? validationErrors = null,
        string? detail = null
    )
    {
        Status = status;
        Environment = environment;
        ValidationErrors = validationErrors ?? NoValidationErrors;
        Detail = detail;
    }

    /// <summary>The typed operation outcome.</summary>
    public WorkSourceEnvironmentOperationStatus Status { get; }

    /// <summary>The safe managed profile for successful read or mutation operations.</summary>
    public WorkSourceEnvironmentProfile? Environment { get; }

    /// <summary>Field-keyed validation errors for invalid input.</summary>
    public IReadOnlyDictionary<string, string[]> ValidationErrors { get; }

    /// <summary>A safe description for not-found or conflict outcomes.</summary>
    public string? Detail { get; }

    public static WorkSourceEnvironmentOperationResult Succeeded(
        WorkSourceEnvironmentProfile? environment = null
    ) => new(WorkSourceEnvironmentOperationStatus.Succeeded, environment);

    public static WorkSourceEnvironmentOperationResult ValidationFailed(
        IReadOnlyDictionary<string, string[]> errors
    ) => new(WorkSourceEnvironmentOperationStatus.ValidationFailed, validationErrors: errors);

    public static WorkSourceEnvironmentOperationResult NotFound(string detail) =>
        new(WorkSourceEnvironmentOperationStatus.NotFound, detail: detail);

    public static WorkSourceEnvironmentOperationResult Conflict(string detail) =>
        new(WorkSourceEnvironmentOperationStatus.Conflict, detail: detail);
}

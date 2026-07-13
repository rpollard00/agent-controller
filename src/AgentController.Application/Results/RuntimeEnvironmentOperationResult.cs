using AgentController.Domain;

namespace AgentController.Application.Results;

/// <summary>Identifies the outcome of a managed runtime environment operation.</summary>
public enum RuntimeEnvironmentOperationStatus
{
    /// <summary>The operation completed successfully.</summary>
    Succeeded,

    /// <summary>The supplied runtime environment data was invalid.</summary>
    ValidationFailed,

    /// <summary>The requested runtime environment does not exist.</summary>
    NotFound,

    /// <summary>The operation conflicts with an existing or referencing resource.</summary>
    Conflict,
}

/// <summary>
/// Application-layer result for managed runtime environment operations. Forwarded environment
/// variables contain names and references only; values are never resolved into this result.
/// </summary>
public sealed record RuntimeEnvironmentOperationResult
{
    private static readonly IReadOnlyDictionary<string, string[]> NoValidationErrors =
        new Dictionary<string, string[]>();

    private RuntimeEnvironmentOperationResult(
        RuntimeEnvironmentOperationStatus status,
        RuntimeEnvironmentProfile? environment = null,
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
    public RuntimeEnvironmentOperationStatus Status { get; }

    /// <summary>The normalized runtime environment for successful operations.</summary>
    public RuntimeEnvironmentProfile? Environment { get; }

    /// <summary>Field-keyed validation errors for invalid input.</summary>
    public IReadOnlyDictionary<string, string[]> ValidationErrors { get; }

    /// <summary>A safe description for not-found or conflict outcomes.</summary>
    public string? Detail { get; }

    public static RuntimeEnvironmentOperationResult Succeeded(
        RuntimeEnvironmentProfile? environment = null
    ) => new(RuntimeEnvironmentOperationStatus.Succeeded, environment);

    public static RuntimeEnvironmentOperationResult ValidationFailed(
        IReadOnlyDictionary<string, string[]> errors
    ) => new(RuntimeEnvironmentOperationStatus.ValidationFailed, validationErrors: errors);

    public static RuntimeEnvironmentOperationResult NotFound(string detail) =>
        new(RuntimeEnvironmentOperationStatus.NotFound, detail: detail);

    public static RuntimeEnvironmentOperationResult Conflict(string detail) =>
        new(RuntimeEnvironmentOperationStatus.Conflict, detail: detail);
}

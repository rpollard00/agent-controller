using AgentController.Domain;

namespace AgentController.Application.Results;

/// <summary>Identifies the outcome of a managed Azure DevOps environment operation.</summary>
public enum AzureDevOpsEnvironmentOperationStatus
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
/// Application-layer result for managed Azure DevOps environment operations. Profiles contain
/// only the name of the environment variable that holds a PAT; credential values are never
/// resolved into this result.
/// </summary>
public sealed record AzureDevOpsEnvironmentOperationResult
{
    private static readonly IReadOnlyDictionary<string, string[]> NoValidationErrors =
        new Dictionary<string, string[]>();

    private AzureDevOpsEnvironmentOperationResult(
        AzureDevOpsEnvironmentOperationStatus status,
        AzureDevOpsEnvironmentProfile? environment = null,
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
    public AzureDevOpsEnvironmentOperationStatus Status { get; }

    /// <summary>The safe managed profile for successful read or mutation operations.</summary>
    public AzureDevOpsEnvironmentProfile? Environment { get; }

    /// <summary>Field-keyed validation errors for invalid input.</summary>
    public IReadOnlyDictionary<string, string[]> ValidationErrors { get; }

    /// <summary>A safe description for not-found or conflict outcomes.</summary>
    public string? Detail { get; }

    public static AzureDevOpsEnvironmentOperationResult Succeeded(
        AzureDevOpsEnvironmentProfile? environment = null
    ) => new(AzureDevOpsEnvironmentOperationStatus.Succeeded, environment);

    public static AzureDevOpsEnvironmentOperationResult ValidationFailed(
        IReadOnlyDictionary<string, string[]> errors
    ) => new(AzureDevOpsEnvironmentOperationStatus.ValidationFailed, validationErrors: errors);

    public static AzureDevOpsEnvironmentOperationResult NotFound(string detail) =>
        new(AzureDevOpsEnvironmentOperationStatus.NotFound, detail: detail);

    public static AzureDevOpsEnvironmentOperationResult Conflict(string detail) =>
        new(AzureDevOpsEnvironmentOperationStatus.Conflict, detail: detail);
}

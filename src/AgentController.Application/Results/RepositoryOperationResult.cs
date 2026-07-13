using AgentController.Domain;

namespace AgentController.Application.Results;

/// <summary>Identifies the outcome of a repository onboarding operation.</summary>
public enum RepositoryOperationStatus
{
    /// <summary>The operation completed successfully.</summary>
    Succeeded,

    /// <summary>The supplied repository data was invalid.</summary>
    ValidationFailed,

    /// <summary>The requested repository does not exist.</summary>
    NotFound,

    /// <summary>The operation conflicts with an existing repository.</summary>
    Conflict,
}

/// <summary>
/// Application-layer result for repository onboarding operations. The result contains no
/// transport-specific concepts so API and worker callers can map it independently.
/// </summary>
public sealed record RepositoryOperationResult
{
    private static readonly IReadOnlyDictionary<string, string[]> NoValidationErrors =
        new Dictionary<string, string[]>();

    private RepositoryOperationResult(
        RepositoryOperationStatus status,
        RepositoryProfile? repository = null,
        IReadOnlyDictionary<string, string[]>? validationErrors = null,
        string? detail = null
    )
    {
        Status = status;
        Repository = repository;
        ValidationErrors = validationErrors ?? NoValidationErrors;
        Detail = detail;
    }

    /// <summary>The typed operation outcome.</summary>
    public RepositoryOperationStatus Status { get; }

    /// <summary>The normalized repository for successful read or mutation operations.</summary>
    public RepositoryProfile? Repository { get; }

    /// <summary>Field-keyed validation errors when <see cref="Status"/> is validation failed.</summary>
    public IReadOnlyDictionary<string, string[]> ValidationErrors { get; }

    /// <summary>A safe description for not-found or conflict outcomes.</summary>
    public string? Detail { get; }

    public static RepositoryOperationResult Succeeded(RepositoryProfile? repository = null) =>
        new(RepositoryOperationStatus.Succeeded, repository);

    public static RepositoryOperationResult ValidationFailed(
        IReadOnlyDictionary<string, string[]> errors
    ) => new(RepositoryOperationStatus.ValidationFailed, validationErrors: errors);

    public static RepositoryOperationResult NotFound(string detail) =>
        new(RepositoryOperationStatus.NotFound, detail: detail);

    public static RepositoryOperationResult Conflict(string detail) =>
        new(RepositoryOperationStatus.Conflict, detail: detail);
}

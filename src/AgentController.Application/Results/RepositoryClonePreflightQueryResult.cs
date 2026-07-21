using AgentController.Domain;

namespace AgentController.Application.Results;

/// <summary>Application outcome for a managed repository clone preflight.</summary>
public sealed record RepositoryClonePreflightQueryResult
{
    private static readonly IReadOnlyDictionary<string, string[]> NoValidationErrors =
        new Dictionary<string, string[]>();

    private RepositoryClonePreflightQueryResult(
        RepositoryOperationStatus status,
        ClonePreflightResult? preflight = null,
        IReadOnlyDictionary<string, string[]>? validationErrors = null,
        string? detail = null
    )
    {
        Status = status;
        Preflight = preflight;
        ValidationErrors = validationErrors ?? NoValidationErrors;
        Detail = detail;
    }

    public RepositoryOperationStatus Status { get; }

    /// <summary>The completed diagnostic, including an actionable failure when it did not pass.</summary>
    public ClonePreflightResult? Preflight { get; }

    public IReadOnlyDictionary<string, string[]> ValidationErrors { get; }

    public string? Detail { get; }

    public static RepositoryClonePreflightQueryResult Succeeded(ClonePreflightResult preflight) =>
        new(RepositoryOperationStatus.Succeeded, preflight);

    public static RepositoryClonePreflightQueryResult ValidationFailed(
        IReadOnlyDictionary<string, string[]> errors
    ) => new(RepositoryOperationStatus.ValidationFailed, validationErrors: errors);

    public static RepositoryClonePreflightQueryResult NotFound(string detail) =>
        new(RepositoryOperationStatus.NotFound, detail: detail);
}

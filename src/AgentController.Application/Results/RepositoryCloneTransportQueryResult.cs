using AgentController.Domain;

namespace AgentController.Application.Results;

/// <summary>Application result for resolving a managed repository's clone transport.</summary>
public sealed record RepositoryCloneTransportQueryResult
{
    private static readonly IReadOnlyDictionary<string, string[]> NoValidationErrors =
        new Dictionary<string, string[]>();

    private RepositoryCloneTransportQueryResult(
        RepositoryOperationStatus status,
        RepositoryCloneTransportResolution? resolution = null,
        IReadOnlyDictionary<string, string[]>? validationErrors = null,
        string? detail = null
    )
    {
        Status = status;
        Resolution = resolution;
        ValidationErrors = validationErrors ?? NoValidationErrors;
        Detail = detail;
    }

    /// <summary>The typed query outcome.</summary>
    public RepositoryOperationStatus Status { get; }

    /// <summary>The resolved transport when the query succeeds.</summary>
    public RepositoryCloneTransportResolution? Resolution { get; }

    /// <summary>Field-keyed validation errors for an invalid repository key.</summary>
    public IReadOnlyDictionary<string, string[]> ValidationErrors { get; }

    /// <summary>A safe description when the repository does not exist.</summary>
    public string? Detail { get; }

    public static RepositoryCloneTransportQueryResult Succeeded(
        RepositoryCloneTransportResolution resolution
    ) => new(RepositoryOperationStatus.Succeeded, resolution);

    public static RepositoryCloneTransportQueryResult ValidationFailed(
        IReadOnlyDictionary<string, string[]> errors
    ) => new(RepositoryOperationStatus.ValidationFailed, validationErrors: errors);

    public static RepositoryCloneTransportQueryResult NotFound(string detail) =>
        new(RepositoryOperationStatus.NotFound, detail: detail);
}

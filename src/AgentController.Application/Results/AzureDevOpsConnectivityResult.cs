using System.Net;

namespace AgentController.Application;

/// <summary>
/// Result of a connectivity verification against an Azure DevOps organization/project.
/// </summary>
public sealed record AzureDevOpsConnectivityResult
{
    /// <summary>Whether the connectivity check succeeded (project endpoint returned 2xx).</summary>
    public bool Success { get; init; }

    /// <summary>HTTP status code from the project endpoint call, or <c>null</c> if the request never completed.</summary>
    public HttpStatusCode? Status { get; init; }

    /// <summary>Human-readable error message when <see cref="Success"/> is <c>false</c>, or <c>null</c> on success.</summary>
    public string? Error { get; init; }

    /// <summary>
    /// Repositories enumerated from the project (populated only when <see cref="Success"/> is <c>true</c>).
    /// May be empty if the project has no Git repositories.
    /// </summary>
    public IReadOnlyList<RepositoryInfo> Repositories { get; init; } = Array.Empty<RepositoryInfo>();
}

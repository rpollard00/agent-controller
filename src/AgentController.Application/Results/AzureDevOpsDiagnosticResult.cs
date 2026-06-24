namespace AgentController.Application.Results;

/// <summary>
/// Result of an Azure DevOps diagnostic run, mirroring the API diagnostic response shape.
/// </summary>
public sealed record AzureDevOpsDiagnosticResult
{
    /// <summary>Overall status: 'ConfigurationError', 'Connected', or 'ConnectionFailed'.</summary>
    public string Status { get; init; } = string.Empty;

    /// <summary>The configured Azure DevOps organization URL, or <c>null</c> if not set.</summary>
    public string? OrganizationUrl { get; init; }

    /// <summary>The configured Azure DevOps project name, or <c>null</c> if not set.</summary>
    public string? Project { get; init; }

    /// <summary>Whether a valid PAT was resolved from configuration.</summary>
    public bool PatConfigured { get; init; }

    /// <summary>HTTP status code from the connectivity check, or <c>null</c> if not attempted.</summary>
    public int? HttpStatusCode { get; init; }

    /// <summary>API error message when the connectivity check failed, or <c>null</c> on success.</summary>
    public string? ApiError { get; init; }

    /// <summary>Enumerated repositories (populated only when status is 'Connected').</summary>
    public IReadOnlyList<AzureDevOpsDiagnosticRepository> Repositories { get; init; } = [];

    /// <summary>Aggregated error messages from configuration validation and API calls.</summary>
    public IReadOnlyList<string> Errors { get; init; } = [];

    /// <summary>Timestamp when the diagnostic was executed.</summary>
    public DateTimeOffset Timestamp { get; init; }
}

/// <summary>
/// Repository metadata included in the diagnostic result.
/// </summary>
public sealed record AzureDevOpsDiagnosticRepository
{
    /// <summary>Repository identifier (GUID string).</summary>
    public string? Id { get; init; }

    /// <summary>Repository name.</summary>
    public string? Name { get; init; }

    /// <summary>Default branch name (e.g. <c>refs/heads/main</c>), or <c>null</c>.</summary>
    public string? DefaultBranch { get; init; }

    /// <summary>Remote URL for cloning, or <c>null</c>.</summary>
    public string? RemoteUrl { get; init; }
}

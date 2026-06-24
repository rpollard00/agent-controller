using AgentController.Application.Abstractions;
using AgentController.Application.Results;

namespace AgentController.Application.Queries;

/// <summary>
/// Handles <see cref="RunAzureDevOpsDiagnosticQuery"/> by validating configuration,
/// resolving the PAT, and delegating connectivity verification to
/// <see cref="IAzureDevOpsBoardsClient"/>.
/// </summary>
public sealed class RunAzureDevOpsDiagnosticQueryHandler(
        IAzureDevOpsDiagnosticConfig diagnosticConfig,
        IAzureDevOpsBoardsClient boardsClient)
    : IQueryHandler<RunAzureDevOpsDiagnosticQuery, AzureDevOpsDiagnosticResult>
{
    public async Task<AzureDevOpsDiagnosticResult> ExecuteAsync(
        RunAzureDevOpsDiagnosticQuery query,
        CancellationToken cancellationToken)
    {
        var errors = new List<string>();

        // (1) Validate required configuration fields
        if (string.IsNullOrWhiteSpace(diagnosticConfig.OrganizationUrl))
        {
            errors.Add("workSource:organizationUrl is not configured.");
        }
        if (string.IsNullOrWhiteSpace(diagnosticConfig.Project))
        {
            errors.Add("workSource:project is not configured.");
        }

        // Resolve the PAT (may throw if ENV: reference is missing)
        string? resolvedPat = null;
        try
        {
            resolvedPat = diagnosticConfig.ResolvePersonalAccessToken();
        }
        catch (InvalidOperationException ex)
        {
            errors.Add($"PAT resolution failed: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(resolvedPat) && errors.TrueForAll(e => !e.StartsWith("PAT resolution failed:", StringComparison.Ordinal)))
        {
            errors.Add("azureDevOps:personalAccessToken is not configured.");
        }

        // If config validation failed, return early without making API calls
        if (errors.Count > 0)
        {
            return new AzureDevOpsDiagnosticResult
            {
                Status = "ConfigurationError",
                OrganizationUrl = diagnosticConfig.OrganizationUrl,
                Project = diagnosticConfig.Project,
                PatConfigured = !string.IsNullOrWhiteSpace(resolvedPat),
                Errors = errors,
                Timestamp = DateTimeOffset.UtcNow,
            };
        }

        // (2) Verify connectivity via the client abstraction
        var connectivityResult = await boardsClient.VerifyConnectivityAsync(
            diagnosticConfig.OrganizationUrl!,
            diagnosticConfig.Project!,
            resolvedPat!,
            cancellationToken);

        // Map repositories from the connectivity result
        var repositories = connectivityResult.Repositories
            .Select(r => new AzureDevOpsDiagnosticRepository
            {
                Id = r.Id,
                Name = r.Name,
                DefaultBranch = r.DefaultBranch,
                RemoteUrl = r.RemoteUrl,
            })
            .ToList();

        if (connectivityResult.Success)
        {
            return new AzureDevOpsDiagnosticResult
            {
                Status = "Connected",
                OrganizationUrl = diagnosticConfig.OrganizationUrl,
                Project = diagnosticConfig.Project,
                PatConfigured = true,
                HttpStatusCode = connectivityResult.Status is { } statusCode ? (int)statusCode : null,
                Repositories = repositories,
                Errors = errors,
                Timestamp = DateTimeOffset.UtcNow,
            };
        }

        // Connection failed — include the error from the client
        if (!string.IsNullOrEmpty(connectivityResult.Error))
        {
            errors.Add(connectivityResult.Error);
        }

        return new AzureDevOpsDiagnosticResult
        {
            Status = "ConnectionFailed",
            OrganizationUrl = diagnosticConfig.OrganizationUrl,
            Project = diagnosticConfig.Project,
            PatConfigured = true,
            HttpStatusCode = connectivityResult.Status is { } statusCode2 ? (int)statusCode2 : null,
            ApiError = connectivityResult.Error,
            Repositories = repositories,
            Errors = errors,
            Timestamp = DateTimeOffset.UtcNow,
        };
    }
}

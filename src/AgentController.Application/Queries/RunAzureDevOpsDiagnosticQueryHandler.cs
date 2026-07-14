using AgentController.Application.Abstractions;
using AgentController.Application.Results;

namespace AgentController.Application.Queries;

/// <summary>
/// Validates the effective Azure DevOps profile, resolves its PAT at the last responsible
/// moment, and delegates connectivity verification to <see cref="IAzureDevOpsBoardsClient"/>.
/// </summary>
public sealed class RunAzureDevOpsDiagnosticQueryHandler(
    IAzureDevOpsDiagnosticConfig diagnosticConfig,
    IAzureDevOpsBoardsClientFactory boardsClientFactory,
    IManagedProfileResolver profileResolver
) : IQueryHandler<RunAzureDevOpsDiagnosticQuery, AzureDevOpsDiagnosticResult>
{
    public async Task<AzureDevOpsDiagnosticResult> ExecuteAsync(
        RunAzureDevOpsDiagnosticQuery query,
        CancellationToken cancellationToken
    )
    {
        var resolvedEnvironment = await profileResolver.ResolveWorkSourceEnvironmentAsync(
            query.EnvironmentKey,
            cancellationToken
        );

        var organizationUrl =
            resolvedEnvironment?.IsManaged == true
                ? resolvedEnvironment.Profile.OrganizationUrl
                : diagnosticConfig.OrganizationUrl;
        var project =
            resolvedEnvironment?.IsManaged == true
                ? resolvedEnvironment.Profile.Project
                : diagnosticConfig.Project;

        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(organizationUrl))
        {
            errors.Add("Azure DevOps organization URL is not configured.");
        }
        if (string.IsNullOrWhiteSpace(project))
        {
            errors.Add("Azure DevOps project is not configured.");
        }

        string? resolvedPat = null;
        try
        {
            resolvedPat =
                resolvedEnvironment?.IsManaged == true
                    ? ResolveManagedPat(resolvedEnvironment.Profile.PatEnvironmentVariable)
                    : diagnosticConfig.ResolvePersonalAccessToken();
        }
        catch (InvalidOperationException ex)
        {
            errors.Add($"PAT resolution failed: {ex.Message}");
        }

        if (
            string.IsNullOrWhiteSpace(resolvedPat)
            && errors.TrueForAll(error =>
                !error.StartsWith("PAT resolution failed:", StringComparison.Ordinal)
            )
        )
        {
            errors.Add("Azure DevOps PAT is not configured.");
        }

        if (errors.Count > 0)
        {
            return new AzureDevOpsDiagnosticResult
            {
                Status = "ConfigurationError",
                OrganizationUrl = organizationUrl,
                Project = project,
                PatConfigured = !string.IsNullOrWhiteSpace(resolvedPat),
                Errors = errors,
                Timestamp = DateTimeOffset.UtcNow,
            };
        }

        var clientProfile =
            resolvedEnvironment?.Profile
            ?? new AgentController.Domain.WorkSourceEnvironmentProfile
            {
                Key = "diagnostic",
                DisplayName = "Diagnostic Azure DevOps environment",
                Enabled = true,
                OrganizationUrl = organizationUrl!,
                Project = project!,
            };
        var boardsClient = boardsClientFactory.Create(clientProfile);
        using var disposableClient = boardsClient as IDisposable;
        var connectivityResult = await boardsClient.VerifyConnectivityAsync(
            organizationUrl!,
            project!,
            resolvedPat!,
            cancellationToken
        );

        var repositories = connectivityResult
            .Repositories.Select(repository => new AzureDevOpsDiagnosticRepository
            {
                Id = repository.Id,
                Name = repository.Name,
                DefaultBranch = repository.DefaultBranch,
                RemoteUrl = repository.RemoteUrl,
            })
            .ToList();

        if (connectivityResult.Success)
        {
            return new AzureDevOpsDiagnosticResult
            {
                Status = "Connected",
                OrganizationUrl = organizationUrl,
                Project = project,
                PatConfigured = true,
                HttpStatusCode = connectivityResult.Status is { } statusCode
                    ? (int)statusCode
                    : null,
                Repositories = repositories,
                Errors = errors,
                Timestamp = DateTimeOffset.UtcNow,
            };
        }

        if (!string.IsNullOrEmpty(connectivityResult.Error))
        {
            errors.Add(connectivityResult.Error);
        }

        return new AzureDevOpsDiagnosticResult
        {
            Status = "ConnectionFailed",
            OrganizationUrl = organizationUrl,
            Project = project,
            PatConfigured = true,
            HttpStatusCode = connectivityResult.Status is { } failedStatusCode
                ? (int)failedStatusCode
                : null,
            ApiError = connectivityResult.Error,
            Repositories = repositories,
            Errors = errors,
            Timestamp = DateTimeOffset.UtcNow,
        };
    }

    private static string ResolveManagedPat(string environmentVariable)
    {
        if (string.IsNullOrWhiteSpace(environmentVariable))
        {
            throw new InvalidOperationException(
                "The managed profile has no PAT environment-variable reference."
            );
        }

        var value = Environment.GetEnvironmentVariable(environmentVariable);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"PAT environment variable '{environmentVariable}' is missing or empty."
            );
        }

        return value;
    }
}

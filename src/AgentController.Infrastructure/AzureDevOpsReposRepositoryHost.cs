using AgentController.Application;
using AgentController.Application.Abstractions;
using AgentController.Application.Results;
using AgentController.Domain;

namespace AgentController.Infrastructure;

/// <summary>
/// Azure DevOps Repos implementation of <see cref="IRepositoryHostConnection"/>.
/// Reuses the existing <see cref="AzureDevOpsBoardsClient"/> for HTTP operations
/// without reimplementing any HTTP machinery. PAT is resolved through
/// <see cref="Domain.Secrets.ISecretStore"/> from the profile's named secret reference.
/// </summary>
internal sealed class AzureDevOpsReposRepositoryHost(
    IAzureDevOpsReposClientFactory clientFactory,
    AzureDevOpsPatResolver patResolver
) : IRepositoryHostConnection
{
    /// <inheritdoc />
    public async Task<RepositoryHostConnectivityResult> VerifyConnectivityAsync(
        RepositoryHostConnectionProfile profile,
        CancellationToken cancellationToken
    )
    {
        // Validate required configuration fields.
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(profile.OrganizationUrl))
        {
            errors.Add("Azure DevOps organization URL is not configured.");
        }
        if (string.IsNullOrWhiteSpace(profile.Project))
        {
            errors.Add("Azure DevOps project is not configured.");
        }

        // Resolve PAT through ISecretStore via the shared PAT resolver.
        string? resolvedPat;
        try
        {
            resolvedPat = await patResolver.ResolveFromSecretReferenceAsync(
                profile.PersonalAccessTokenReference,
                cancellationToken
            );
        }
        catch (Exception ex)
        {
            errors.Add($"PAT resolution failed: {ex.Message}");
            resolvedPat = null;
        }

        if (string.IsNullOrWhiteSpace(resolvedPat) && errors.Count == 0)
        {
            errors.Add(
                profile.PersonalAccessTokenReference.IsSpecified
                    ? $"Secret '{profile.PersonalAccessTokenReference.Name}' could not be resolved."
                    : "Azure DevOps PAT is not configured."
            );
        }

        if (errors.Count > 0)
        {
            return RepositoryHostConnectivityResult.FailureResult(
                errors,
                authMechanism: "PersonalAccessToken"
            );
        }

        // Build client via factory and verify connectivity through the existing ADO client.
        var adoClient = clientFactory.Create(profile, resolvedPat!);
        using var disposableClient = adoClient as IDisposable;

        var connectivityResult = await adoClient.VerifyConnectivityAsync(
            profile.OrganizationUrl,
            profile.Project,
            resolvedPat!,
            cancellationToken
        );

        // Map repositories into a serializable payload.
        var repositories = connectivityResult.Repositories.Select(repo =>
            new Dictionary<string, object?>
            {
                ["id"] = repo.Id,
                ["name"] = repo.Name,
                ["defaultBranch"] = repo.DefaultBranch,
                ["remoteUrl"] = repo.RemoteUrl,
            }
        ).ToList();

        if (connectivityResult.Success)
        {
            var payload = new Dictionary<string, object>
            {
                ["repositories"] = repositories,
            };

            return RepositoryHostConnectivityResult.SuccessResult(
                "PersonalAccessToken",
                connectivityResult.Status is { } statusCode ? (int)statusCode : null,
                payload
            );
        }

        // Connectivity failed — collect errors from the ADO result.
        var connectErrors = new List<string>();
        if (!string.IsNullOrEmpty(connectivityResult.Error))
        {
            connectErrors.Add(connectivityResult.Error);
        }

        return RepositoryHostConnectivityResult.FailureResult(
            connectErrors,
            authMechanism: "PersonalAccessToken",
            httpStatus: connectivityResult.Status is { } failedStatusCode
                ? (int)failedStatusCode
                : null,
            payload: new Dictionary<string, object> { ["repositories"] = repositories }
        );
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<HostRepository>> ListRepositoriesAsync(
        RepositoryHostConnectionProfile profile,
        CancellationToken cancellationToken
    )
    {
        // Validate required configuration fields.
        if (string.IsNullOrWhiteSpace(profile.OrganizationUrl) ||
            string.IsNullOrWhiteSpace(profile.Project))
        {
            return Array.Empty<HostRepository>();
        }

        // Resolve PAT through ISecretStore via the shared PAT resolver.
        string? resolvedPat;
        try
        {
            resolvedPat = await patResolver.ResolveFromSecretReferenceAsync(
                profile.PersonalAccessTokenReference,
                cancellationToken
            );
        }
        catch
        {
            return Array.Empty<HostRepository>();
        }

        if (string.IsNullOrWhiteSpace(resolvedPat))
        {
            return Array.Empty<HostRepository>();
        }

        // Build client via factory and list repositories through the existing ADO client.
        var adoClient = clientFactory.Create(profile, resolvedPat!);
        using var disposableClient = adoClient as IDisposable;

        var repositories = await adoClient.ListRepositoriesAsync(
            profile.Project,
            cancellationToken
        );

        // Map RepositoryInfo records into provider-neutral HostRepository records.
        return repositories.Select(repo => new HostRepository(
            Id: repo.Id,
            Name: repo.Name,
            DefaultBranch: StripRefsHeads(repo.DefaultBranch),
            RemoteUrl: repo.RemoteUrl ?? string.Empty,
            CloneTransportHint: CloneTransportHint.HttpsPat
        )).ToList();
    }

    /// <summary>
    /// Strip the "refs/heads/" prefix from a branch name if present.
    /// E.g. "refs/heads/main" → "main".
    /// </summary>
    private static string StripRefsHeads(string? branch)
    {
        if (string.IsNullOrWhiteSpace(branch))
            return string.Empty;

        const string prefix = "refs/heads/";
        if (branch.StartsWith(prefix, StringComparison.Ordinal))
        {
            return branch[prefix.Length..];
        }

        return branch;
    }
}

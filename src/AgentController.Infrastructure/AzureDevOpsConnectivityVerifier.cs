using AgentController.Application;
using AgentController.Application.Abstractions;
using AgentController.Application.Results;
using AgentController.Domain;

namespace AgentController.Infrastructure;

/// <summary>
/// Azure DevOps implementation of <see cref="IWorkSourceConnectivityVerifier"/>.
/// Uses <see cref="IAzureDevOpsBoardsClientFactory"/> to build a client from the
/// resolved <see cref="WorkSourceEnvironmentProfile"/>, then calls
/// <see cref="IAzureDevOpsBoardsClient.VerifyConnectivityAsync"/> and maps the
/// result (including enumerated repositories) into the provider-neutral
/// <see cref="WorkSourceConnectivityResult"/>.
///
/// PAT resolution is routed through <see cref="AzureDevOpsPatResolver"/> which
/// resolves named secrets via <see cref="Domain.Secrets.ISecretStore"/>.
/// </summary>
internal sealed class AzureDevOpsConnectivityVerifier(
    IAzureDevOpsBoardsClientFactory boardsClientFactory,
    AzureDevOpsPatResolver patResolver
) : IWorkSourceConnectivityVerifier
{
    public async Task<WorkSourceConnectivityResult> VerifyAsync(
        WorkSourceEnvironmentProfile profile,
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

        // Validate that a secret reference is configured.
        if (!profile.PersonalAccessTokenReference.IsSpecified)
        {
            errors.Add("The managed profile has no PAT secret reference configured.");
        }

        // Resolve PAT via ISecretStore.
        string? resolvedPat;
        try
        {
            resolvedPat = await patResolver.ResolveFromSecretReferenceAsync(
                profile.PersonalAccessTokenReference,
                cancellationToken
            );

            if (string.IsNullOrWhiteSpace(resolvedPat))
            {
                errors.Add(
                    profile.PersonalAccessTokenReference.IsSpecified
                        ? $"Secret '{profile.PersonalAccessTokenReference.Name}' could not be resolved."
                        : "Azure DevOps PAT is not configured."
                );
            }
        }
        catch (Exception ex)
        {
            errors.Add($"PAT resolution failed: {ex.Message}");
            resolvedPat = null;
        }

        if (errors.Count > 0)
        {
            return WorkSourceConnectivityResult.FailureResult(
                errors,
                authMechanism: "PersonalAccessToken"
            );
        }

        // Build client via factory and verify connectivity.
        var boardsClient = boardsClientFactory.Create(profile);
        using var disposableClient = boardsClient as IDisposable;

        var connectivityResult = await boardsClient.VerifyConnectivityAsync(
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

            return WorkSourceConnectivityResult.SuccessResult(
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

        return WorkSourceConnectivityResult.FailureResult(
            connectErrors,
            authMechanism: "PersonalAccessToken",
            httpStatus: connectivityResult.Status is { } failedStatusCode
                ? (int)failedStatusCode
                : null,
            payload: new Dictionary<string, object> { ["repositories"] = repositories }
        );
    }
}

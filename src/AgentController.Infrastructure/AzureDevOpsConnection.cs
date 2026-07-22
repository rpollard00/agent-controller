using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AgentController.Application;
using AgentController.Application.Abstractions;
using AgentController.Application.Results;
using AgentController.Domain;
using Microsoft.Extensions.Logging;

namespace AgentController.Infrastructure;

/// <summary>
/// Azure DevOps implementation of <see cref="IConnection"/>.
///
/// Reuses the proven verify path via <see cref="IAzureDevOpsBoardsClient.VerifyConnectivityAsync"/>
/// (org-level; returns an organization-scoped payload, no repository enumeration),
/// adds org-level project enumeration, and reuses the existing client repo listing.
/// </summary>
internal sealed partial class AzureDevOpsConnection(
    AzureDevOpsClientFactory clientFactory,
    AzureDevOpsPatResolver patResolver,
    ILogger<AzureDevOpsConnection> logger,
    HttpMessageHandler? httpHandler = null
) : IConnection
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public async Task<ConnectionConnectivityResult> VerifyConnectivityAsync(
        ConnectionProfile profile,
        CancellationToken cancellationToken
    )
    {
        // Extract ADO settings from the connection profile.
        var settings = profile.ProviderSettings as AzureDevOpsConnectionSettings;
        if (settings is null)
        {
            return ConnectionConnectivityResult.FailureResult(
                ["Connection profile has no Azure DevOps settings configured."],
                authMechanism: "PersonalAccessToken"
            );
        }

        var organizationUrl = settings.OrganizationUrl;
        var patReference = settings.PersonalAccessTokenReference;

        // Validate required configuration fields.
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(organizationUrl))
        {
            errors.Add("Azure DevOps organization URL is not configured.");
        }

        // Validate that a secret reference is configured.
        if (!patReference.IsSpecified)
        {
            errors.Add("The managed profile has no PAT secret reference configured.");
        }

        // Resolve PAT via ISecretStore.
        string? resolvedPat;
        try
        {
            resolvedPat = await patResolver.ResolveFromSecretReferenceAsync(
                patReference,
                cancellationToken
            );

            if (string.IsNullOrWhiteSpace(resolvedPat))
            {
                errors.Add(
                    patReference.IsSpecified
                        ? $"Secret '{patReference.Name}' could not be resolved."
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
            return ConnectionConnectivityResult.FailureResult(
                errors,
                authMechanism: "PersonalAccessToken"
            );
        }

        // Build client via factory and verify connectivity.
        // The connection is org-level — we use a lightweight project query to validate org + PAT.
        // IAzureDevOpsBoardsClient.VerifyConnectivityAsync requires a project parameter;
        // we pass an empty string and let the ADO API validate org-level auth.
        var boardsClient = clientFactory.Create(
            organizationUrl,
            string.Empty, // org-level verify — project not required
            resolvedPat!
        );
        using var disposableClient = boardsClient as IDisposable;

        var connectivityResult = await boardsClient.VerifyConnectivityAsync(
            organizationUrl,
            string.Empty,
            resolvedPat!,
            cancellationToken
        );

        // Org-level connection: the payload describes the organization scope,
        // not repositories (repo enumeration is project-scoped and not performed here).
        var payload = new Dictionary<string, object>
        {
            ["scope"] = "organization",
            ["organizationUrl"] = organizationUrl,
        };

        if (connectivityResult.Success)
        {
            return ConnectionConnectivityResult.SuccessResult(
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

        return ConnectionConnectivityResult.FailureResult(
            connectErrors,
            authMechanism: "PersonalAccessToken",
            httpStatus: connectivityResult.Status is { } failedStatusCode
                ? (int)failedStatusCode
                : null,
            payload: payload
        );
    }

    public async Task<IReadOnlyList<ConnectionProject>> ListProjectsAsync(
        ConnectionProfile profile,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var settings = profile.ProviderSettings as AzureDevOpsConnectionSettings;
            if (settings is null || string.IsNullOrWhiteSpace(settings.OrganizationUrl))
            {
                return Array.Empty<ConnectionProject>();
            }

            var patReference = settings.PersonalAccessTokenReference;
            if (!patReference.IsSpecified)
            {
                return Array.Empty<ConnectionProject>();
            }

            // Resolve PAT.
            string? resolvedPat;
            try
            {
                resolvedPat = await patResolver.ResolveFromSecretReferenceAsync(
                    patReference,
                    cancellationToken
                );
            }
            catch
            {
                return Array.Empty<ConnectionProject>();
            }

            if (string.IsNullOrWhiteSpace(resolvedPat))
            {
                return Array.Empty<ConnectionProject>();
            }

            // Call ADO org-level projects API: GET {OrganizationUrl}/_apis/projects
            using var http = httpHandler is { }
                ? new HttpClient(httpHandler) { BaseAddress = new Uri(settings.OrganizationUrl.TrimEnd('/') + "/") }
                : new HttpClient { BaseAddress = new Uri(settings.OrganizationUrl.TrimEnd('/') + "/") };

            var authBytes = Encoding.ASCII.GetBytes($":{resolvedPat}");
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Basic",
                Convert.ToBase64String(authBytes)
            );
            http.DefaultRequestHeaders.Accept.Clear();
            http.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json")
            );

            var response = await http.GetAsync(
                "_apis/projects?api-version=7.1",
                cancellationToken
            );

            if (!response.IsSuccessStatusCode)
            {
                Log.ProjectsListFailed(
                    logger,
                    (int)response.StatusCode,
                    response.ReasonPhrase ?? "(no reason phrase)"
                );
                return Array.Empty<ConnectionProject>();
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);

            var projects = new List<ConnectionProject>();

            if (
                doc.RootElement.TryGetProperty("value", out var valueArray)
                && valueArray.ValueKind == JsonValueKind.Array
            )
            {
                foreach (var project in valueArray.EnumerateArray())
                {
                    var id =
                        project.TryGetProperty("id", out var idEl)
                        && idEl.ValueKind == JsonValueKind.String
                            ? idEl.GetString() ?? string.Empty
                            : string.Empty;

                    var name =
                        project.TryGetProperty("name", out var nameEl)
                        && nameEl.ValueKind == JsonValueKind.String
                            ? nameEl.GetString() ?? string.Empty
                            : string.Empty;

                    if (!string.IsNullOrEmpty(id) || !string.IsNullOrEmpty(name))
                    {
                        projects.Add(new ConnectionProject(id, name));
                    }
                }
            }

            return projects;
        }
        catch (OperationCanceledException)
        {
            return Array.Empty<ConnectionProject>();
        }
        catch (Exception ex)
        {
            Log.ProjectsListException(logger, ex);
            return Array.Empty<ConnectionProject>();
        }
    }

    public async Task<IReadOnlyList<HostRepository>> ListRepositoriesAsync(
        ConnectionProfile profile,
        string project,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var settings = profile.ProviderSettings as AzureDevOpsConnectionSettings;
            if (settings is null || string.IsNullOrWhiteSpace(settings.OrganizationUrl))
            {
                return Array.Empty<HostRepository>();
            }

            if (string.IsNullOrWhiteSpace(project))
            {
                return Array.Empty<HostRepository>();
            }

            var patReference = settings.PersonalAccessTokenReference;
            if (!patReference.IsSpecified)
            {
                return Array.Empty<HostRepository>();
            }

            // Resolve PAT.
            string? resolvedPat;
            try
            {
                resolvedPat = await patResolver.ResolveFromSecretReferenceAsync(
                    patReference,
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

            // Build client via factory and list repositories.
            var boardsClient = clientFactory.Create(
                settings.OrganizationUrl,
                project,
                resolvedPat
            );
            using var disposableClient = boardsClient as IDisposable;

            var repositories = await boardsClient.ListRepositoriesAsync(
                project,
                cancellationToken
            );

            // Map RepositoryInfo records into provider-neutral HostRepository records.
            return repositories.Select(repo => new HostRepository(
                Id: repo.Id,
                Name: repo.Name,
                DefaultBranch: StripRefsHeads(repo.DefaultBranch),
                RemoteUrl: repo.RemoteUrl ?? string.Empty,
                SshUrl: repo.SshUrl,
                CloneTransportHint: CloneTransportHint.HttpsPat
            )).ToList();
        }
        catch (OperationCanceledException)
        {
            return Array.Empty<HostRepository>();
        }
        catch (Exception ex)
        {
            Log.RepositoriesListException(logger, project, ex);
            return Array.Empty<HostRepository>();
        }
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

    // ─── LoggerMessage definitions ───────────────────────────────

    private static partial class Log
    {
        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Failed to list ADO projects: HTTP {StatusCode} {ReasonPhrase}."
        )]
        public static partial void ProjectsListFailed(
            ILogger logger, int statusCode, string reasonPhrase
        );

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Failed to list ADO projects."
        )]
        public static partial void ProjectsListException(ILogger logger, Exception ex);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Failed to list ADO repositories for project '{Project}'."
        )]
        public static partial void RepositoriesListException(
            ILogger logger, string project, Exception ex
        );
    }
}

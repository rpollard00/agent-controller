using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AgentController.Application;
using AgentController.Infrastructure.Options;

namespace AgentController.Infrastructure;

/// <summary>
/// <see cref="IPrLabelSource"/> implementation against the Azure DevOps Git REST API.
///
/// Calls <c>GET {project}/_apis/git/repositories/{repository}/pullRequests/{pullRequestId}/labels?api-version=7.1</c>
/// to fetch labels for a pull request.
/// </summary>
internal sealed class AzureDevOpsReposPrLabelSource : IPrLabelSource
{
    private readonly HttpClient _http;
    private readonly AzureDevOpsBoardsOptions _options;

    public AzureDevOpsReposPrLabelSource(HttpClient http, AzureDevOpsBoardsOptions options)
    {
        _http = http;
        _options = options;

        // Configure base address from options (same pattern as AzureDevOpsReposFeedbackSource).
        if (!string.IsNullOrWhiteSpace(options.BaseUrl))
        {
            _http.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
        }

        // Set Basic auth header with PAT.
        var pat = options.ResolvePersonalAccessToken();
        if (!string.IsNullOrWhiteSpace(pat))
        {
            var authBytes = Encoding.ASCII.GetBytes($":{pat}");
            _http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Basic", Convert.ToBase64String(authBytes));
        }
    }

    public async Task<IReadOnlyList<PrLabel>> GetLabelsAsync(
        PrUnderTest pr,
        CancellationToken cancellationToken)
    {
        // Parse project and repository from the ADO PR URL (same logic as AzureDevOpsReposFeedbackSource).
        var urlParts = ParsePullRequestUrl(pr.PullRequestUrl);
        if (urlParts is null)
        {
            return [];
        }

        var (project, repository) = urlParts.Value;

        var endpoint = $"{project}/_apis/git/repositories/{Uri.EscapeDataString(repository)}/pullRequests/{Uri.EscapeDataString(pr.PullRequestId)}/labels?api-version=7.1";

        return await FetchLabelsAsync(endpoint, cancellationToken);
    }

    private async Task<IReadOnlyList<PrLabel>> FetchLabelsAsync(
        string endpoint,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await _http.GetAsync(endpoint, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return [];
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);

            var labels = new List<PrLabel>();

            if (!doc.RootElement.TryGetProperty("value", out var valueArray)
                || valueArray.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            foreach (var label in valueArray.EnumerateArray())
            {
                var name = label.TryGetProperty("name", out var nameEl)
                           && nameEl.ValueKind == JsonValueKind.String
                    ? nameEl.GetString() ?? string.Empty
                    : string.Empty;

                var createdBy = string.Empty;
                if (label.TryGetProperty("createdBy", out var createdByEl)
                    && createdByEl.ValueKind == JsonValueKind.Object
                    && createdByEl.TryGetProperty("uniqueName", out var uniqueNameEl)
                    && uniqueNameEl.ValueKind == JsonValueKind.String)
                {
                    createdBy = uniqueNameEl.GetString() ?? string.Empty;
                }

                labels.Add(new PrLabel
                {
                    Name = name,
                    CreatedBy = createdBy,
                });
            }

            return labels;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Any error — return empty list (marker gate fails-closed).
            return [];
        }
    }

    /// <summary>
    /// Parse an Azure DevOps pull request URL to extract project and repository name.
    /// Mirrors the logic in AzureDevOpsReposFeedbackSource.
    /// </summary>
    private static (string Project, string Repository)? ParsePullRequestUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var segments = uri.Segments;

            for (int i = 0; i < segments.Length; i++)
            {
                if (segments[i].Equals("_git/", StringComparison.Ordinal))
                {
                    if (i > 0 && i + 1 < segments.Length)
                    {
                        var project = segments[i - 1].TrimEnd('/');
                        var repository = segments[i + 1].TrimEnd('/');

                        if (!string.IsNullOrWhiteSpace(project) && !string.IsNullOrWhiteSpace(repository))
                        {
                            return (project, repository);
                        }
                    }

                    break;
                }
            }
        }

        return null;
    }
}

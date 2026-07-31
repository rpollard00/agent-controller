using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AgentController.Application;

namespace AgentController.Infrastructure;

/// <summary>Creates authenticated Azure DevOps pull-request label clients.</summary>
internal abstract class AzureDevOpsPullRequestLabelClientFactory
{
    public abstract AzureDevOpsPullRequestLabelClient Create(
        string organizationUrl,
        string personalAccessToken
    );
}

/// <summary>Default HTTP-client factory for Azure DevOps pull-request labels.</summary>
internal sealed class DefaultAzureDevOpsPullRequestLabelClientFactory
    : AzureDevOpsPullRequestLabelClientFactory
{
    public override AzureDevOpsPullRequestLabelClient Create(
        string organizationUrl,
        string personalAccessToken
    ) => new(new HttpClient(), organizationUrl, personalAccessToken);
}

/// <summary>
/// Narrow Azure DevOps Repos REST client for pull-request label reads and mutations.
/// </summary>
internal sealed class AzureDevOpsPullRequestLabelClient : IDisposable
{
    private readonly HttpClient _http;

    public AzureDevOpsPullRequestLabelClient(
        HttpClient http,
        string organizationUrl,
        string personalAccessToken
    )
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentException.ThrowIfNullOrWhiteSpace(organizationUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(personalAccessToken);

        _http = http;
        _http.BaseAddress = new Uri(organizationUrl.TrimEnd('/') + "/", UriKind.Absolute);
        var authBytes = Encoding.ASCII.GetBytes($":{personalAccessToken}");
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(authBytes)
        );
        _http.DefaultRequestHeaders.Accept.Clear();
        _http.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json")
        );
    }

    /// <summary>Reads the labels currently applied to one pull request.</summary>
    public async Task<IReadOnlyList<PrLabel>> GetLabelsAsync(
        AzureDevOpsManagedRepository repository,
        string pullRequestId,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(pullRequestId);
        cancellationToken.ThrowIfCancellationRequested();

        var labels = await FetchAsync(repository, pullRequestId, cancellationToken);
        return labels
            .Select(label => new PrLabel { Name = label.Name })
            .ToArray();
    }

    /// <summary>
    /// Applies an idempotent, case-insensitive label mutation to one pull request.
    /// A concurrent delete that returns 404 and a concurrent add that returns 409
    /// are treated as already-applied outcomes.
    /// </summary>
    public async Task MutateAsync(
        AzureDevOpsManagedRepository repository,
        string pullRequestId,
        PullRequestLabelMutation mutation,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(pullRequestId);
        ArgumentNullException.ThrowIfNull(mutation);
        cancellationToken.ThrowIfCancellationRequested();

        var labelsToRemove = NormalizeLabels(mutation.LabelsToRemove);
        var labelsToAdd = NormalizeLabels(mutation.LabelsToAdd);
        if (labelsToRemove.Length == 0 && labelsToAdd.Length == 0)
        {
            return;
        }

        var currentLabels = (await FetchAsync(
            repository,
            pullRequestId,
            cancellationToken
        )).ToList();

        // Apply removals first. If the same logical label is also added, the
        // addition below wins as documented by the application contract.
        foreach (var label in labelsToRemove)
        {
            var matches = currentLabels
                .Where(current => current.Name.Equals(
                    label,
                    StringComparison.OrdinalIgnoreCase
                ))
                .ToArray();

            foreach (var match in matches)
            {
                await DeleteAsync(
                    repository,
                    pullRequestId,
                    match.Identifier,
                    cancellationToken
                );
                currentLabels.Remove(match);
            }
        }

        foreach (var label in labelsToAdd)
        {
            if (currentLabels.Any(current => current.Name.Equals(
                label,
                StringComparison.OrdinalIgnoreCase
            )))
            {
                continue;
            }

            await AddAsync(repository, pullRequestId, label, cancellationToken);
            currentLabels.Add(new AzureDevOpsPullRequestLabel(label, label));
        }
    }

    public void Dispose() => _http.Dispose();

    private async Task<IReadOnlyList<AzureDevOpsPullRequestLabel>> FetchAsync(
        AzureDevOpsManagedRepository repository,
        string pullRequestId,
        CancellationToken cancellationToken
    )
    {
        var endpoint = BuildLabelsEndpoint(repository, pullRequestId);
        using var response = await _http.GetAsync(endpoint, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw CreateProviderException(
                "read labels",
                repository.RepositoryKey,
                pullRequestId,
                response.StatusCode
            );
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: cancellationToken
        );
        if (!document.RootElement.TryGetProperty("value", out var values)
            || values.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException(
                $"Azure DevOps returned an invalid label response for pull request "
                    + $"'{pullRequestId}' in managed repository '{repository.RepositoryKey}'."
            );
        }

        var labels = new List<AzureDevOpsPullRequestLabel>();
        foreach (var value in values.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var name = ReadJsonString(value, "name");
            if (name.Length == 0)
            {
                continue;
            }

            var identifier = ReadJsonIdentifier(value, "id");
            labels.Add(new AzureDevOpsPullRequestLabel(
                name,
                identifier.Length > 0 ? identifier : name
            ));
        }

        return labels;
    }

    private async Task DeleteAsync(
        AzureDevOpsManagedRepository repository,
        string pullRequestId,
        string labelIdentifier,
        CancellationToken cancellationToken
    )
    {
        var endpoint = BuildLabelsEndpoint(
            repository,
            pullRequestId,
            labelIdentifier
        );
        using var response = await _http.DeleteAsync(endpoint, cancellationToken);
        if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound)
        {
            return;
        }

        throw CreateProviderException(
            "remove a label",
            repository.RepositoryKey,
            pullRequestId,
            response.StatusCode
        );
    }

    private async Task AddAsync(
        AzureDevOpsManagedRepository repository,
        string pullRequestId,
        string label,
        CancellationToken cancellationToken
    )
    {
        var endpoint = BuildLabelsEndpoint(repository, pullRequestId);
        using var content = JsonContent.Create(new { name = label });
        using var response = await _http.PostAsync(endpoint, content, cancellationToken);
        if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.Conflict)
        {
            return;
        }

        throw CreateProviderException(
            "add a label",
            repository.RepositoryKey,
            pullRequestId,
            response.StatusCode
        );
    }

    private static string BuildLabelsEndpoint(
        AzureDevOpsManagedRepository repository,
        string pullRequestId,
        string? labelIdentifier = null
    )
    {
        var endpoint = $"{Uri.EscapeDataString(repository.Project)}/_apis/git/repositories/"
            + $"{Uri.EscapeDataString(repository.RemoteIdentity)}/pullRequests/"
            + $"{Uri.EscapeDataString(pullRequestId)}/labels";
        if (!string.IsNullOrWhiteSpace(labelIdentifier))
        {
            endpoint += $"/{Uri.EscapeDataString(labelIdentifier)}";
        }

        return endpoint + "?api-version=7.1";
    }

    private static HttpRequestException CreateProviderException(
        string operation,
        string repositoryKey,
        string pullRequestId,
        HttpStatusCode statusCode
    ) => new(
        $"Azure DevOps failed to {operation} for pull request '{pullRequestId}' "
            + $"in managed repository '{repositoryKey}': HTTP {(int)statusCode}.",
        inner: null,
        statusCode: statusCode
    );

    private static string[] NormalizeLabels(IEnumerable<string> labels) =>
        labels
            .Select(label => label?.Trim() ?? string.Empty)
            .Where(label => label.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string ReadJsonString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String
                ? property.GetString()?.Trim() ?? string.Empty
                : string.Empty;
    }

    private static string ReadJsonIdentifier(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return string.Empty;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString()?.Trim() ?? string.Empty,
            JsonValueKind.Number => property.GetRawText(),
            _ => string.Empty,
        };
    }

    private sealed record AzureDevOpsPullRequestLabel(
        string Name,
        string Identifier
    );
}

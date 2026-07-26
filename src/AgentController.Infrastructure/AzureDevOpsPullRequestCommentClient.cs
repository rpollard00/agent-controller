using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AgentController.Application;

namespace AgentController.Infrastructure;

/// <summary>Creates authenticated Azure DevOps pull-request comment clients.</summary>
internal abstract class AzureDevOpsPullRequestCommentClientFactory
{
    public abstract AzureDevOpsPullRequestCommentClient Create(
        string organizationUrl,
        string personalAccessToken
    );
}

/// <summary>Default HTTP-client factory for Azure DevOps pull-request comments.</summary>
internal sealed class DefaultAzureDevOpsPullRequestCommentClientFactory
    : AzureDevOpsPullRequestCommentClientFactory
{
    public override AzureDevOpsPullRequestCommentClient Create(
        string organizationUrl,
        string personalAccessToken
    ) => new(new HttpClient(), organizationUrl, personalAccessToken);
}

/// <summary>Narrow Azure DevOps Repos REST client for idempotent PR thread creation.</summary>
internal sealed class AzureDevOpsPullRequestCommentClient : IDisposable
{
    private readonly HttpClient _http;

    public AzureDevOpsPullRequestCommentClient(
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

    /// <summary>
    /// Creates one PR-level thread unless its deterministic correlation marker is already
    /// present. Checking provider state before writing makes retries and restart recovery
    /// idempotent, including retries after a successful write whose response was lost.
    /// </summary>
    public async Task CreateAsync(
        AzureDevOpsManagedRepository repository,
        string pullRequestId,
        AssistanceLifecycleCommentRequest request,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(pullRequestId);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var rendered = AssistanceLifecycleCommentRenderer.Render(request);
        if (await ContainsMarkerAsync(
            repository,
            pullRequestId,
            rendered.IdempotencyMarker,
            cancellationToken
        ))
        {
            return;
        }

        var endpoint = BuildThreadsEndpoint(repository, pullRequestId);
        using var content = JsonContent.Create(new
        {
            comments = new[]
            {
                new
                {
                    parentCommentId = 0,
                    content = rendered.Content,
                    commentType = 1,
                },
            },
            status = 1,
        });
        using var response = await _http.PostAsync(endpoint, content, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        // A provider or proxy can report a failed response after accepting the thread.
        // Reconcile once before surfacing the error so that a retry cannot duplicate it.
        if (await ContainsMarkerAsync(
            repository,
            pullRequestId,
            rendered.IdempotencyMarker,
            cancellationToken
        ))
        {
            return;
        }

        throw CreateProviderException(
            "create a lifecycle comment",
            repository.RepositoryKey,
            pullRequestId,
            response.StatusCode
        );
    }

    public void Dispose() => _http.Dispose();

    private async Task<bool> ContainsMarkerAsync(
        AzureDevOpsManagedRepository repository,
        string pullRequestId,
        string marker,
        CancellationToken cancellationToken
    )
    {
        var endpoint = BuildThreadsEndpoint(repository, pullRequestId);
        using var response = await _http.GetAsync(endpoint, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw CreateProviderException(
                "read lifecycle comments",
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
        if (!document.RootElement.TryGetProperty("value", out var threads)
            || threads.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException(
                $"Azure DevOps returned an invalid thread response for pull request "
                    + $"'{pullRequestId}' in managed repository '{repository.RepositoryKey}'."
            );
        }

        foreach (var thread in threads.EnumerateArray())
        {
            if (!thread.TryGetProperty("comments", out var comments)
                || comments.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var comment in comments.EnumerateArray())
            {
                if (IsDeleted(comment)
                    || !comment.TryGetProperty("content", out var commentContent)
                    || commentContent.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                if ((commentContent.GetString() ?? string.Empty).Contains(
                    marker,
                    StringComparison.Ordinal
                ))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsDeleted(JsonElement comment) =>
        comment.TryGetProperty("isDeleted", out var deleted)
        && deleted.ValueKind is JsonValueKind.True;

    private static string BuildThreadsEndpoint(
        AzureDevOpsManagedRepository repository,
        string pullRequestId
    ) => $"{Uri.EscapeDataString(repository.Project)}/_apis/git/repositories/"
        + $"{Uri.EscapeDataString(repository.RemoteIdentity)}/pullRequests/"
        + $"{Uri.EscapeDataString(pullRequestId)}/threads?api-version=7.1";

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
}

using System.Net;
using System.Text;
using System.Text.Json;
using AgentController.Application;
using AgentController.Domain;
using AgentController.Infrastructure;
using AgentController.Infrastructure.Options;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentController.Infrastructure.Tests;

public sealed class AzureDevOpsAssistanceStoryCreationTests
{
    private const string OrganizationUrl = "https://dev.azure.com/example";

    [Fact]
    public async Task CreateWorkItemAsync_PostsEncodedJsonPatchWithTagsAndRelations()
    {
        HttpMethod? method = null;
        Uri? requestUri = null;
        string? mediaType = null;
        string? requestBody = null;
        const string description = "<p>Resolve \"quoted\" feedback &amp; keep PR #17.</p>\n<p>Next line.</p>";
        var handler = new DelegateHandler(async request =>
        {
            method = request.Method;
            requestUri = request.RequestUri;
            mediaType = request.Content?.Headers.ContentType?.MediaType;
            requestBody = await request.Content!.ReadAsStringAsync();

            return JsonResponse(
                HttpStatusCode.OK,
                new
                {
                    id = 501,
                    rev = 7,
                    fields = new Dictionary<string, object>
                    {
                        ["System.Title"] = "Continue PR 17",
                        ["System.Description"] = description,
                        ["System.State"] = "New",
                        ["System.Tags"] = "repo:widgets; custom-ready-rework; assistance:cycle-17",
                        ["System.WorkItemType"] = "User Story",
                    },
                    _links = new
                    {
                        html = new
                        {
                            href = "https://dev.azure.com/example/Project/_workitems/edit/501",
                        },
                    },
                }
            );
        });
        using var client = CreateClient(handler, "Project");

        var result = await client.CreateWorkItemAsync(
            new BoardsCreateWorkItemParameters
            {
                Project = "Project",
                WorkItemType = "User Story",
                RepoKey = "widgets",
                Title = "Continue PR 17",
                Description = description,
                Tags = ["repo:widgets", "custom-ready-rework", "assistance:cycle-17"],
                Relations =
                [
                    new WorkItemRelation
                    {
                        RelationType = "System.LinkTypes.Related",
                        Url = "https://dev.azure.com/example/_apis/wit/workItems/41",
                        Attributes = new Dictionary<string, string>
                        {
                            ["comment"] = "Source story",
                        },
                    },
                    new WorkItemRelation
                    {
                        RelationType = "ArtifactLink",
                        Url = "vstfs:///Git/PullRequestId/project%2Frepo%2F17",
                        Attributes = new Dictionary<string, string>
                        {
                            ["name"] = "Pull Request",
                        },
                    },
                ],
            },
            CancellationToken.None
        );

        Assert.Equal(HttpMethod.Post, method);
        Assert.Equal(
            "https://dev.azure.com/example/Project/_apis/wit/workitems/$User%20Story?api-version=7.1",
            requestUri?.AbsoluteUri
        );
        Assert.Equal("application/json-patch+json", mediaType);
        Assert.NotNull(requestBody);

        using var patch = JsonDocument.Parse(requestBody);
        var operations = patch.RootElement.EnumerateArray().ToList();
        Assert.Equal("Continue PR 17", OperationValue(operations, "/fields/System.Title"));
        Assert.Equal(description, OperationValue(operations, "/fields/System.Description"));
        Assert.Equal(
            "repo:widgets; custom-ready-rework; assistance:cycle-17",
            OperationValue(operations, "/fields/System.Tags")
        );
        var relations = operations
            .Where(operation => operation.GetProperty("path").GetString() == "/relations/-")
            .ToList();
        Assert.Equal(2, relations.Count);
        Assert.Equal(
            "System.LinkTypes.Related",
            relations[0].GetProperty("value").GetProperty("rel").GetString()
        );
        Assert.Equal(
            "Source story",
            relations[0]
                .GetProperty("value")
                .GetProperty("attributes")
                .GetProperty("comment")
                .GetString()
        );
        Assert.Equal(
            "Pull Request",
            relations[1]
                .GetProperty("value")
                .GetProperty("attributes")
                .GetProperty("name")
                .GetString()
        );

        Assert.Equal("501", result.ExternalId);
        Assert.Equal("7", result.Revision);
        Assert.Equal("https://dev.azure.com/example/Project/_workitems/edit/501", result.Url);
        Assert.Equal("wi_501", result.Candidate.Id);
        Assert.Equal("widgets", result.Candidate.RepoKey);
        Assert.Equal(description, result.Candidate.Description);
        Assert.Equal("New", result.Candidate.Status);
        Assert.Equal("7", result.Candidate.SourceMetadata?["revision"]);
        Assert.Equal("User Story", result.Candidate.SourceMetadata?["workItemType"]);
        Assert.Equal(
            ["repo:widgets", "custom-ready-rework", "assistance:cycle-17"],
            result.Candidate.Tags
        );
    }

    [Fact]
    public async Task CreateWorkItemAsync_UsesConfiguredTypeAndMapsSparseResponse()
    {
        Uri? requestUri = null;
        var handler = new DelegateHandler(request =>
        {
            requestUri = request.RequestUri;
            return Task.FromResult(
                JsonResponse(
                    HttpStatusCode.Created,
                    new
                    {
                        id = "88",
                        rev = "2",
                        url = "https://dev.azure.com/example/Project/_apis/wit/workItems/88",
                    }
                )
            );
        });
        using var client = CreateClient(handler, "Project");

        var result = await client.CreateWorkItemAsync(
            new BoardsCreateWorkItemParameters
            {
                Project = "Project",
                WorkItemType = "Product Backlog Item",
                RepoKey = "api",
                Title = "Continue existing pull request",
                Description = "<p>No unresolved comments.</p>",
                Tags = ["repo:api", "agent-ready-rework", "assistance:cycle-88"],
            },
            CancellationToken.None
        );

        Assert.Equal(
            "https://dev.azure.com/example/Project/_apis/wit/workitems/$Product%20Backlog%20Item?api-version=7.1",
            requestUri?.AbsoluteUri
        );
        Assert.Equal("88", result.ExternalId);
        Assert.Equal("2", result.Revision);
        Assert.Equal("https://dev.azure.com/example/Project/_workitems/edit/88", result.Url);
        Assert.Equal("Continue existing pull request", result.Candidate.Title);
        Assert.Equal("<p>No unresolved comments.</p>", result.Candidate.Description);
        Assert.Equal(
            ["repo:api", "agent-ready-rework", "assistance:cycle-88"],
            result.Candidate.Tags
        );
    }

    private static string? OperationValue(
        IReadOnlyList<JsonElement> operations,
        string path
    ) => operations
        .Single(operation => operation.GetProperty("path").GetString() == path)
        .GetProperty("value")
        .GetString();

    private static AzureDevOpsBoardsClient CreateClient(
        HttpMessageHandler handler,
        string project
    )
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri(OrganizationUrl + "/") };
        return new AzureDevOpsBoardsClient(
            http,
            new AzureDevOpsBoardsOptions
            {
                BaseUrl = OrganizationUrl,
                Project = project,
                PersonalAccessToken = "test-pat",
            },
            NullLogger<AzureDevOpsBoardsClient>.Instance
        );
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, object body) =>
        new(status)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(body),
                Encoding.UTF8,
                "application/json"
            ),
        };

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> handler
    ) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => handler(request);
    }
}

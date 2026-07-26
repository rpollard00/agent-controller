using System.Net;
using System.Text;
using System.Text.Json;
using AgentController.Application;
using AgentController.Infrastructure;

namespace AgentController.Infrastructure.Tests;

public sealed class AzureDevOpsAssistanceStoryRelationshipTests
{
    private const string OrganizationUrl = "https://dev.azure.com/example";
    private const string TestPat = "relationship-test-pat-must-not-leak";
    private const string Parent90Url =
        "https://dev.azure.com/example/_apis/wit/workItems/90";

    [Fact]
    public async Task Client_PrefersOriginatingControllerWorkItem()
    {
        var requestedUris = new List<string>();
        var handler = new DelegateHandler(request =>
        {
            var uri = request.RequestUri?.AbsoluteUri ?? string.Empty;
            requestedUris.Add(uri);
            Assert.DoesNotContain(TestPat, request.Headers.Authorization?.Parameter ?? string.Empty);

            if (IsWorkItemDetails(uri))
            {
                Assert.Contains("ids=700", uri, StringComparison.OrdinalIgnoreCase);
                return JsonResponse(new
                {
                    value = new[]
                    {
                        WorkItem(700, "Bug", Parent90Url),
                    },
                });
            }

            Assert.DoesNotContain("/workitems?", uri, StringComparison.OrdinalIgnoreCase);
            return JsonResponse(PullRequest());
        });
        using var client = CreateClient(handler);

        var resolved = await client.ResolveAsync(
            Repository(),
            "17",
            "700",
            CancellationToken.None
        );

        Assert.Equal(["700"], resolved.SourceStoryIds);
        Assert.Equal(Parent90Url, resolved.CommonParentUrl);
        Assert.Equal(3, resolved.Relations.Count);
        AssertRelation(
            resolved.Relations[0],
            "System.LinkTypes.Related",
            "https://dev.azure.com/example/_apis/wit/workItems/700"
        );
        AssertRelation(
            resolved.Relations[1],
            "System.LinkTypes.Hierarchy-Reverse",
            Parent90Url
        );
        AssertPullRequestArtifact(resolved.Relations[2]);
        Assert.Equal(2, requestedUris.Count);
    }

    [Fact]
    public async Task Client_FallsBackWhenOriginatingWorkItemIsUnavailable()
    {
        var handler = new DelegateHandler(request =>
        {
            var uri = request.RequestUri?.AbsoluteUri ?? string.Empty;
            if (IsWorkItemDetails(uri))
            {
                return uri.Contains("ids=700", StringComparison.OrdinalIgnoreCase)
                    ? JsonResponse(new { value = Array.Empty<object>() })
                    : JsonResponse(new
                    {
                        value = new[] { WorkItem(41, "User Story", parentUrl: null) },
                    });
            }

            if (uri.Contains("/workitems?", StringComparison.OrdinalIgnoreCase))
            {
                return JsonResponse(new { value = new[] { new { id = 41 } } });
            }

            return JsonResponse(PullRequest());
        });
        using var client = CreateClient(handler);

        var resolved = await client.ResolveAsync(
            Repository(),
            "17",
            "700",
            CancellationToken.None
        );

        Assert.Equal(["41"], resolved.SourceStoryIds);
        Assert.Null(resolved.CommonParentUrl);
        AssertRelation(
            resolved.Relations[0],
            "System.LinkTypes.Related",
            "https://dev.azure.com/example/_apis/wit/workItems/41"
        );
        AssertPullRequestArtifact(resolved.Relations[1]);
    }

    [Fact]
    public async Task Client_FallsBackToEveryLinkedUserStoryAndInheritsCommonParent()
    {
        var handler = new DelegateHandler(request =>
        {
            var uri = request.RequestUri?.AbsoluteUri ?? string.Empty;
            if (IsWorkItemDetails(uri))
            {
                return JsonResponse(new
                {
                    value = new[]
                    {
                        WorkItem(43, "Bug", Parent90Url),
                        WorkItem(42, "user story", Parent90Url),
                        WorkItem(41, "User Story", Parent90Url),
                    },
                });
            }

            if (uri.Contains("/workitems?", StringComparison.OrdinalIgnoreCase))
            {
                return JsonResponse(new
                {
                    value = new object[]
                    {
                        new { id = 43 },
                        new { id = 41 },
                        new { id = 42 },
                        new { id = 41 },
                    },
                });
            }

            return JsonResponse(PullRequest());
        });
        using var client = CreateClient(handler);

        var resolved = await client.ResolveAsync(
            Repository(),
            "17",
            originatingWorkItemId: null,
            CancellationToken.None
        );

        Assert.Equal(["41", "42"], resolved.SourceStoryIds);
        Assert.Equal(Parent90Url, resolved.CommonParentUrl);
        Assert.Equal(4, resolved.Relations.Count);
        Assert.All(
            resolved.Relations.Take(2),
            relation => Assert.Equal("System.LinkTypes.Related", relation.RelationType)
        );
        Assert.EndsWith("/41", resolved.Relations[0].Url, StringComparison.Ordinal);
        Assert.EndsWith("/42", resolved.Relations[1].Url, StringComparison.Ordinal);
        AssertRelation(
            resolved.Relations[2],
            "System.LinkTypes.Hierarchy-Reverse",
            Parent90Url
        );
        AssertPullRequestArtifact(resolved.Relations[3]);
    }

    [Fact]
    public async Task Client_DoesNotAssignParentUnlessEverySourceSharesIt()
    {
        var handler = new DelegateHandler(request =>
        {
            var uri = request.RequestUri?.AbsoluteUri ?? string.Empty;
            if (IsWorkItemDetails(uri))
            {
                return JsonResponse(new
                {
                    value = new[]
                    {
                        WorkItem(41, "User Story", Parent90Url),
                        WorkItem(
                            42,
                            "User Story",
                            "https://dev.azure.com/example/_apis/wit/workItems/91"
                        ),
                    },
                });
            }

            if (uri.Contains("/workitems?", StringComparison.OrdinalIgnoreCase))
            {
                return JsonResponse(new { value = new[] { new { id = 41 }, new { id = 42 } } });
            }

            return JsonResponse(PullRequest());
        });
        using var client = CreateClient(handler);

        var resolved = await client.ResolveAsync(
            Repository(),
            "17",
            originatingWorkItemId: null,
            CancellationToken.None
        );

        Assert.Null(resolved.CommonParentUrl);
        Assert.DoesNotContain(
            resolved.Relations,
            relation => relation.RelationType == "System.LinkTypes.Hierarchy-Reverse"
        );
        Assert.Equal(2, resolved.Relations.Count(relation =>
            relation.RelationType == "System.LinkTypes.Related"
        ));
        AssertPullRequestArtifact(resolved.Relations[^1]);
    }

    [Fact]
    public async Task Client_HumanPullRequestWithoutLinkedStoriesStillGetsArtifactLink()
    {
        var workItemDetailsRequested = false;
        var handler = new DelegateHandler(request =>
        {
            var uri = request.RequestUri?.AbsoluteUri ?? string.Empty;
            if (IsWorkItemDetails(uri))
            {
                workItemDetailsRequested = true;
            }

            return uri.Contains("/workitems?", StringComparison.OrdinalIgnoreCase)
                ? JsonResponse(new { value = Array.Empty<object>() })
                : JsonResponse(PullRequest());
        });
        using var client = CreateClient(handler);

        var resolved = await client.ResolveAsync(
            Repository(),
            "17",
            originatingWorkItemId: null,
            CancellationToken.None
        );

        Assert.Empty(resolved.SourceStoryIds);
        Assert.Null(resolved.CommonParentUrl);
        AssertPullRequestArtifact(Assert.Single(resolved.Relations));
        Assert.False(workItemDetailsRequested);
    }

    [Fact]
    public async Task NoOpResolver_HonorsCancellation()
    {
        var resolver = new NoOpAssistanceStoryRelationshipResolver();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => resolver.ResolveAsync(
            new AssistanceStoryRelationshipRequest(),
            cancellation.Token
        ));
    }

    private static void AssertRelation(
        AgentController.Domain.WorkItemRelation relation,
        string relationType,
        string url
    )
    {
        Assert.Equal(relationType, relation.RelationType);
        Assert.Equal(url, relation.Url);
    }

    private static void AssertPullRequestArtifact(
        AgentController.Domain.WorkItemRelation relation
    )
    {
        AssertRelation(
            relation,
            "ArtifactLink",
            "vstfs:///Git/PullRequestId/project-id%2Frepository-id%2F17"
        );
        Assert.Equal("Pull Request", relation.Attributes?["name"]);
    }

    private static bool IsWorkItemDetails(string uri) =>
        uri.Contains("/_apis/wit/workitems?", StringComparison.OrdinalIgnoreCase);

    private static object PullRequest() => new
    {
        pullRequestId = 17,
        repository = new
        {
            id = "repository-id",
            project = new { id = "project-id" },
        },
    };

    private static object WorkItem(int id, string workItemType, string? parentUrl) => new
    {
        id,
        url = $"https://dev.azure.com/example/_apis/wit/workItems/{id}",
        fields = new Dictionary<string, object>
        {
            ["System.WorkItemType"] = workItemType,
        },
        relations = parentUrl is null
            ? Array.Empty<object>()
            : new object[]
            {
                new
                {
                    rel = "System.LinkTypes.Hierarchy-Reverse",
                    url = parentUrl,
                },
            },
    };

    private static AzureDevOpsManagedRepository Repository() => new()
    {
        EnvironmentKey = "ado-production",
        RepositoryKey = "widgets",
        Project = "Widgets Project",
        RemoteIdentity = "widgets-id",
    };

    private static AzureDevOpsAssistanceStoryRelationshipClient CreateClient(
        HttpMessageHandler handler
    ) => new(new HttpClient(handler), OrganizationUrl, TestPat);

    private static HttpResponseMessage JsonResponse(object body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            JsonSerializer.Serialize(body),
            Encoding.UTF8,
            "application/json"
        ),
    };

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, HttpResponseMessage> handler
    ) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => Task.FromResult(handler(request));
    }
}

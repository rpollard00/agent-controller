using System.Net;
using System.Text;
using System.Text.Json;
using AgentController.Application;

namespace AgentController.Infrastructure.Tests;

public sealed class AzureDevOpsPullRequestCommentCreatorTests
{
    private const string OrganizationUrl = "https://dev.azure.com/example";
    private const string TestPat = "test-pat-must-not-leak";

    [Fact]
    public async Task Client_PostsEachLifecycleOncePerCorrelation()
    {
        var comments = new List<string>();
        var methods = new List<HttpMethod>();
        var handler = new DelegateHandler(async (request, cancellationToken) =>
        {
            methods.Add(request.Method);
            Assert.Equal("Basic", request.Headers.Authorization?.Scheme);
            Assert.DoesNotContain(TestPat, request.Headers.Authorization?.Parameter ?? string.Empty);

            if (request.Method == HttpMethod.Get)
            {
                return JsonResponse(JsonSerializer.Serialize(new
                {
                    value = comments.Select((content, index) => new
                    {
                        id = index + 1,
                        comments = new[] { new { content, isDeleted = false } },
                    }),
                }));
            }

            Assert.Equal(HttpMethod.Post, request.Method);
            var json = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(json);
            Assert.Equal(1, document.RootElement.GetProperty("status").GetInt32());
            var comment = document.RootElement
                .GetProperty("comments")
                .EnumerateArray()
                .Single();
            Assert.Equal(0, comment.GetProperty("parentCommentId").GetInt32());
            Assert.Equal(1, comment.GetProperty("commentType").GetInt32());
            comments.Add(comment.GetProperty("content").GetString()!);
            return new HttpResponseMessage(HttpStatusCode.Created);
        });
        using var client = CreateClient(handler);

        foreach (var kind in Enum.GetValues<AssistanceLifecycleCommentKind>())
        {
            var request = Request(kind);
            await client.CreateAsync(Repository(), "42", request, CancellationToken.None);
            await client.CreateAsync(Repository(), "42", request, CancellationToken.None);
        }

        Assert.Equal(4, comments.Count);
        Assert.Equal(8, methods.Count(method => method == HttpMethod.Get));
        Assert.Equal(4, methods.Count(method => method == HttpMethod.Post));
        Assert.All(comments, content => Assert.Contains(
            "[User Story 501](<https://dev.azure.com/example/Project/_workitems/edit/501>)",
            content,
            StringComparison.Ordinal
        ));
        Assert.Equal(4, comments
            .Select(content => content[(content.IndexOf("<!--", StringComparison.Ordinal))..])
            .Distinct(StringComparer.Ordinal)
            .Count());
    }

    [Fact]
    public async Task Client_ReconcilesFailedPostThatProviderAccepted()
    {
        string? acceptedContent = null;
        var handler = new DelegateHandler(async (request, cancellationToken) =>
        {
            if (request.Method == HttpMethod.Get)
            {
                return acceptedContent is null
                    ? JsonResponse("{\"value\":[]}")
                    : JsonResponse(JsonSerializer.Serialize(new
                    {
                        value = new[]
                        {
                            new
                            {
                                comments = new[] { new { content = acceptedContent } },
                            },
                        },
                    }));
            }

            var json = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(json);
            acceptedContent = document.RootElement
                .GetProperty("comments")[0]
                .GetProperty("content")
                .GetString();
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        });
        using var client = CreateClient(handler);

        await client.CreateAsync(
            Repository(),
            "42",
            Request(AssistanceLifecycleCommentKind.Queued),
            CancellationToken.None
        );

        Assert.NotNull(acceptedContent);
    }

    [Fact]
    public async Task Client_UsesEscapedThreadsEndpointAndSurfacesProviderFailure()
    {
        string? requestedUri = null;
        var handler = new DelegateHandler(request =>
        {
            requestedUri = request.RequestUri?.AbsoluteUri;
            return new HttpResponseMessage(HttpStatusCode.Forbidden);
        });
        using var client = CreateClient(handler);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.CreateAsync(
                Repository(),
                "42/7",
                Request(AssistanceLifecycleCommentKind.Queued),
                CancellationToken.None
            )
        );

        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
        Assert.Contains(
            "Payments%20Project/_apis/git/repositories/payments-id/pullRequests/42%2F7/threads?api-version=7.1",
            requestedUri,
            StringComparison.OrdinalIgnoreCase
        );
        Assert.DoesNotContain(TestPat, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoOpCreator_HonorsCancellation()
    {
        var creator = new NoOpPullRequestCommentCreator();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            creator.CreateAsync(new(), Request(AssistanceLifecycleCommentKind.Queued), cancellation.Token)
        );
    }

    private static AssistanceLifecycleCommentRequest Request(
        AssistanceLifecycleCommentKind kind
    ) => new()
    {
        CorrelationId = "correlation-501",
        Kind = kind,
        AssistanceStoryId = "501",
        AssistanceStoryUrl = "https://dev.azure.com/example/Project/_workitems/edit/501",
    };

    private static AzureDevOpsManagedRepository Repository() => new()
    {
        EnvironmentKey = "ado-production",
        RepositoryKey = "payments",
        Project = "Payments Project",
        RemoteIdentity = "payments-id",
    };

    private static AzureDevOpsPullRequestCommentClient CreateClient(
        HttpMessageHandler handler
    ) => new(new HttpClient(handler), OrganizationUrl, TestPat);

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private sealed class DelegateHandler : HttpMessageHandler
    {
        private readonly Func<
            HttpRequestMessage,
            CancellationToken,
            Task<HttpResponseMessage>
        > _handler;

        public DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = (request, _) => Task.FromResult(handler(request));
        }

        public DelegateHandler(
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler
        )
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => _handler(request, cancellationToken);
    }
}

namespace AgentController.Application.Tests;

public sealed class PullRequestCommentCreatorContractTests
{
    [Theory]
    [InlineData(AssistanceLifecycleCommentKind.Queued, "is queued")]
    [InlineData(AssistanceLifecycleCommentKind.Completed, "completed")]
    [InlineData(AssistanceLifecycleCommentKind.Failed, "failed")]
    [InlineData(AssistanceLifecycleCommentKind.NeedsHuman, "needs human attention")]
    public void Renderer_LinksStoryAndCreatesCorrelationAwareMarker(
        AssistanceLifecycleCommentKind kind,
        string expectedMessage
    )
    {
        var request = Request(kind);

        var first = AssistanceLifecycleCommentRenderer.Render(request);
        var retry = AssistanceLifecycleCommentRenderer.Render(request);

        Assert.Equal(first, retry);
        Assert.Contains(expectedMessage, first.Content, StringComparison.Ordinal);
        Assert.Contains(
            "[User Story 501](<https://dev.azure.com/example/Project/_workitems/edit/501>)",
            first.Content,
            StringComparison.Ordinal
        );
        Assert.Contains("correlation%2Fone", first.IdempotencyMarker, StringComparison.Ordinal);
        Assert.EndsWith(first.IdempotencyMarker, first.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Renderer_GivesEachLifecycleMilestoneADistinctIdempotencyMarker()
    {
        var markers = Enum.GetValues<AssistanceLifecycleCommentKind>()
            .Select(kind => AssistanceLifecycleCommentRenderer.Render(Request(kind)).IdempotencyMarker)
            .ToArray();

        Assert.Equal(markers.Length, markers.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData(null, "501", "https://example.test/story/501")]
    [InlineData("correlation", null, "https://example.test/story/501")]
    [InlineData("correlation", "501", "javascript:alert(1)")]
    public void Renderer_RejectsIncompleteOrUnsafeRequests(
        string? correlationId,
        string? storyId,
        string storyUrl
    )
    {
        var request = new AssistanceLifecycleCommentRequest
        {
            CorrelationId = correlationId!,
            Kind = AssistanceLifecycleCommentKind.Queued,
            AssistanceStoryId = storyId!,
            AssistanceStoryUrl = storyUrl,
        };

        Assert.ThrowsAny<ArgumentException>(() =>
            AssistanceLifecycleCommentRenderer.Render(request)
        );
    }

    private static AssistanceLifecycleCommentRequest Request(
        AssistanceLifecycleCommentKind kind
    ) => new()
    {
        CorrelationId = "correlation/one",
        Kind = kind,
        AssistanceStoryId = "501",
        AssistanceStoryUrl = "https://dev.azure.com/example/Project/_workitems/edit/501",
    };
}

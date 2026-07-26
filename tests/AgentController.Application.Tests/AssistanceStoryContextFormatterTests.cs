using AgentController.Domain;

namespace AgentController.Application.Tests;

public sealed class AssistanceStoryContextFormatterTests
{
    [Fact]
    public void Render_DirectsExistingPrWorkAndIncludesCanonicalReferences()
    {
        var rendered = AssistanceStoryContextFormatter.Render(Request());

        Assert.Equal("Assist PR 42: Fix checkout edge cases", rendered.Title);
        Assert.Contains(
            "Continue the existing pull request and update its source branch. "
                + "Do not create or open another pull request.",
            rendered.Description,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "<a href=\"https://dev.azure.com/example/Project/_git/widgets/pullrequest/42\">"
                + "PR 42 — Fix checkout edge cases</a>",
            rendered.Description,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "<strong>Repository:</strong> <code>widgets</code>",
            rendered.Description,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "<strong>Source branch:</strong> <code>refs/heads/fix-checkout</code>",
            rendered.Description,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "<strong>Target branch:</strong> <code>refs/heads/main</code>",
            rendered.Description,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "<strong>Observed source commit:</strong> <code>abc123</code>",
            rendered.Description,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "<strong>Assistance cycle:</strong> <code>2</code>",
            rendered.Description,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void Render_OrdersUnresolvedThreadsByFileAndLineAndIncludesFullReplyChains()
    {
        var request = Request() with
        {
            QualifyingThreads =
            [
                Thread("thread-b30", "src/B.cs", 30, "B line 30"),
                Thread("resolved", "src/A.cs", 1, "Do not render") with
                {
                    Status = ReviewThreadStatus.Resolved,
                },
                Thread("thread-a20", "src/A.cs", 20, "A line 20"),
                new ReviewThread
                {
                    ThreadId = "thread-pr",
                    Status = ReviewThreadStatus.Active,
                    Comments =
                    [
                        new ReviewThreadComment
                        {
                            Author = "reviewer@example.test",
                            Body = "PR-level request",
                            CreatedAt = new DateTimeOffset(2026, 7, 26, 1, 0, 0, TimeSpan.Zero),
                        },
                    ],
                },
                new ReviewThread
                {
                    ThreadId = "thread-a10",
                    Status = ReviewThreadStatus.Active,
                    FilePath = "src/A.cs",
                    StartLine = 10,
                    EndLine = 12,
                    Comments =
                    [
                        new ReviewThreadComment
                        {
                            Author = "reviewer@example.test",
                            Body = "Root comment",
                            CreatedAt = new DateTimeOffset(2026, 7, 26, 1, 0, 0, TimeSpan.Zero),
                        },
                        new ReviewThreadComment
                        {
                            Author = "author@example.test",
                            Body = "Reply from the author",
                            CreatedAt = new DateTimeOffset(2026, 7, 26, 1, 5, 0, TimeSpan.Zero),
                            IsReply = true,
                        },
                        new ReviewThreadComment
                        {
                            Author = "reviewer@example.test",
                            Body = "Final reviewer reply",
                            CreatedAt = new DateTimeOffset(2026, 7, 26, 1, 10, 0, TimeSpan.Zero),
                            IsReply = true,
                        },
                    ],
                },
            ],
        };

        var description = AssistanceStoryContextFormatter.Render(request).Description;

        Assert.Equal(4, Occurrences(description, "— thread <code>"));
        Assert.True(
            description.IndexOf("thread-pr", StringComparison.Ordinal)
                < description.IndexOf("thread-a10", StringComparison.Ordinal)
        );
        Assert.True(
            description.IndexOf("thread-a10", StringComparison.Ordinal)
                < description.IndexOf("thread-a20", StringComparison.Ordinal)
        );
        Assert.True(
            description.IndexOf("thread-a20", StringComparison.Ordinal)
                < description.IndexOf("thread-b30", StringComparison.Ordinal)
        );
        Assert.DoesNotContain("Do not render", description, StringComparison.Ordinal);
        Assert.Contains("<code>src/A.cs</code>, lines 10–12", description, StringComparison.Ordinal);
        Assert.True(
            description.IndexOf("Root comment", StringComparison.Ordinal)
                < description.IndexOf("Reply from the author", StringComparison.Ordinal)
        );
        Assert.True(
            description.IndexOf("Reply from the author", StringComparison.Ordinal)
                < description.IndexOf("Final reviewer reply", StringComparison.Ordinal)
        );
        Assert.Contains(
            "<strong>Reply from author@example.test</strong>",
            description,
            StringComparison.Ordinal
        );
        Assert.Contains("2026-07-26T01:05:00Z", description, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_HtmlEncodesProviderAndReviewerContent()
    {
        var request = Request() with
        {
            PullRequestTitle = "Fix <checkout> & \"payment\"",
            PullRequest = Request().PullRequest with
            {
                RepositoryKey = "widgets<&>",
                SourceBranch = "refs/heads/fix<&>",
                PullRequestUrl = "https://example.test/pr/42?left=1&right=2",
            },
            QualifyingThreads =
            [
                new ReviewThread
                {
                    ThreadId = "thread<&>",
                    Status = ReviewThreadStatus.Active,
                    FilePath = "src/<Checkout&Payment>.cs",
                    StartLine = 9,
                    Comments =
                    [
                        new ReviewThreadComment
                        {
                            Author = "<reviewer&example.test>",
                            Body = "<script>alert(\"unsafe\")</script> & 'review'",
                            CreatedAt = DateTimeOffset.UnixEpoch,
                        },
                    ],
                },
            ],
        };

        var description = AssistanceStoryContextFormatter.Render(request).Description;

        Assert.Contains("Fix &lt;checkout&gt; &amp; &quot;payment&quot;", description, StringComparison.Ordinal);
        Assert.Contains("widgets&lt;&amp;&gt;", description, StringComparison.Ordinal);
        Assert.Contains("left=1&amp;right=2", description, StringComparison.Ordinal);
        Assert.Contains("src/&lt;Checkout&amp;Payment&gt;.cs", description, StringComparison.Ordinal);
        Assert.Contains("thread&lt;&amp;&gt;", description, StringComparison.Ordinal);
        Assert.Contains("&lt;reviewer&amp;example.test&gt;", description, StringComparison.Ordinal);
        Assert.Contains(
            "&lt;script&gt;alert(&quot;unsafe&quot;)&lt;/script&gt; &amp; &#39;review&#39;",
            description,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain("<script>", description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Render_SupportsCleanupRequestWithoutComments()
    {
        var first = AssistanceStoryContextFormatter.Render(Request());
        var retry = AssistanceStoryContextFormatter.Render(Request());

        Assert.Equal(first, retry);
        Assert.Contains(
            "No qualifying unresolved review threads were included.",
            first.Description,
            StringComparison.Ordinal
        );
        Assert.Contains("clean up the existing pull request", first.Description, StringComparison.Ordinal);
        Assert.DoesNotContain("<h3>", first.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_NormalizesAndBoundsTheStoryTitle()
    {
        var rendered = AssistanceStoryContextFormatter.Render(
            Request() with { PullRequestTitle = $"  Fix\n\tcheckout   {new string('x', 300)}  " }
        );

        Assert.StartsWith("Assist PR 42: Fix checkout ", rendered.Title, StringComparison.Ordinal);
        Assert.EndsWith("…", rendered.Title, StringComparison.Ordinal);
        Assert.Equal(255, rendered.Title.Length);
        Assert.DoesNotContain('\n', rendered.Title);
        Assert.DoesNotContain('\t', rendered.Title);
    }

    [Theory]
    [InlineData(0, "https://example.test/pr/42")]
    [InlineData(1, "javascript:alert(1)")]
    public void Render_RejectsInvalidCycleOrUnsafePullRequestUrl(int cycle, string url)
    {
        var request = Request() with
        {
            CycleNumber = cycle,
            PullRequest = Request().PullRequest with { PullRequestUrl = url },
        };

        Assert.ThrowsAny<ArgumentException>(() => AssistanceStoryContextFormatter.Render(request));
    }

    private static AssistanceStoryContextRequest Request() => new()
    {
        PullRequestTitle = "Fix checkout edge cases",
        PullRequest = new PullRequestReference
        {
            EnvironmentKey = "production",
            RepositoryKey = "widgets",
            PullRequestId = "42",
            PullRequestUrl = "https://dev.azure.com/example/Project/_git/widgets/pullrequest/42",
            SourceBranch = "refs/heads/fix-checkout",
            TargetBranch = "refs/heads/main",
            SourceCommitSha = "abc123",
        },
        CycleNumber = 2,
    };

    private static ReviewThread Thread(
        string threadId,
        string filePath,
        int line,
        string body
    ) => new()
    {
        ThreadId = threadId,
        Status = ReviewThreadStatus.Active,
        FilePath = filePath,
        StartLine = line,
        EndLine = line,
        Comments =
        [
            new ReviewThreadComment
            {
                Author = "reviewer@example.test",
                Body = body,
                CreatedAt = DateTimeOffset.UnixEpoch,
            },
        ],
    };

    private static int Occurrences(string value, string search)
    {
        var count = 0;
        var index = 0;
        while ((index = value.IndexOf(search, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += search.Length;
        }

        return count;
    }
}

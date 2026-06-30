using AgentController.Application;
using AgentController.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentController.Application.Tests;

public class ReviewFeedbackFilterPipelineTests
{
    private static ReviewFeedbackFilterPipeline CreatePipeline(IPrLabelSource labelSource)
    {
        return new ReviewFeedbackFilterPipeline(
            labelSource,
            NullLogger<ReviewFeedbackFilterPipeline>.Instance);
    }

    // ── Allowlist fail-closed ──────────────────────────────────────

    [Fact]
    public async Task FilterAsync_EmptyAllowlist_ReturnsEmpty()
    {
        var pipeline = CreatePipeline(new TestPrLabelSource([]));
        var query = new FeedbackQuery
        {
            OpenPrs = [],
            AllowedReviewers = new HashSet<string>(), // Empty — fail-closed
            ReworkMarkerTag = "agent-rework-requested",
        };

        var signals = new List<ReworkSignal>
        {
            new()
            {
                PullRequestId = "1",
                Threads = new List<ReviewThread>
                {
                    new()
                    {
                        ThreadId = "t1",
                        Status = ReviewThreadStatus.Active,
                        Comments = new List<ReviewThreadComment>
                        {
                            new() { Author = "a@example.com", Body = "fix this" },
                        },
                    },
                },
            },
        };

        var result = await pipeline.FilterAsync(query, signals, CancellationToken.None);

        Assert.Empty(result);
    }

    // ── Marker gate ────────────────────────────────────────────────

    [Fact]
    public async Task FilterAsync_NoMarkerLabel_FailsClosed()
    {
        var pipeline = CreatePipeline(new TestPrLabelSource(
            new Dictionary<string, List<PrLabel>>
            {
                ["1"] = [], // No labels
            }));

        var query = new FeedbackQuery
        {
            OpenPrs = new List<PrUnderTest>
            {
                new() { PullRequestId = "1", PullRequestUrl = "https://example.com/pr/1" },
            },
            AllowedReviewers = new HashSet<string> { "reviewer@example.com" },
            ReworkMarkerTag = "agent-rework-requested",
        };

        var signals = new List<ReworkSignal>
        {
            new()
            {
                PullRequestId = "1",
                Threads = ActiveThread("t1", "reviewer@example.com"),
            },
        };

        var result = await pipeline.FilterAsync(query, signals, CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task FilterAsync_MarkerLabelByNonAllowedReviewer_FailsClosed()
    {
        var pipeline = CreatePipeline(new TestPrLabelSource(
            new Dictionary<string, List<PrLabel>>
            {
                ["1"] = new()
                {
                    new() { Name = "agent-rework-requested", CreatedBy = "other@example.com" },
                },
            }));

        var query = new FeedbackQuery
        {
            OpenPrs = new List<PrUnderTest>
            {
                new() { PullRequestId = "1", PullRequestUrl = "https://example.com/pr/1" },
            },
            AllowedReviewers = new HashSet<string> { "reviewer@example.com" },
            ReworkMarkerTag = "agent-rework-requested",
        };

        var signals = new List<ReworkSignal>
        {
            new()
            {
                PullRequestId = "1",
                Threads = ActiveThread("t1", "reviewer@example.com"),
            },
        };

        var result = await pipeline.FilterAsync(query, signals, CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task FilterAsync_MarkerLabelByAllowedReviewer_Passes()
    {
        var pipeline = CreatePipeline(new TestPrLabelSource(
            new Dictionary<string, List<PrLabel>>
            {
                ["1"] = new()
                {
                    new() { Name = "agent-rework-requested", CreatedBy = "reviewer@example.com" },
                },
            }));

        var query = new FeedbackQuery
        {
            OpenPrs = new List<PrUnderTest>
            {
                new() { PullRequestId = "1", PullRequestUrl = "https://example.com/pr/1" },
            },
            AllowedReviewers = new HashSet<string> { "reviewer@example.com" },
            ReworkMarkerTag = "agent-rework-requested",
        };

        var signals = new List<ReworkSignal>
        {
            new()
            {
                PullRequestId = "1",
                Threads = ActiveThread("t1", "reviewer@example.com"),
            },
        };

        var result = await pipeline.FilterAsync(query, signals, CancellationToken.None);

        Assert.Single(result);
        Assert.Single(result[0].Threads);
    }

    [Fact]
    public async Task FilterAsync_MarkerLabelMissingCreator_FailsClosed()
    {
        var pipeline = CreatePipeline(new TestPrLabelSource(
            new Dictionary<string, List<PrLabel>>
            {
                ["1"] = new()
                {
                    new() { Name = "agent-rework-requested", CreatedBy = string.Empty },
                },
            }));

        var query = new FeedbackQuery
        {
            OpenPrs = new List<PrUnderTest>
            {
                new() { PullRequestId = "1", PullRequestUrl = "https://example.com/pr/1" },
            },
            AllowedReviewers = new HashSet<string> { "reviewer@example.com" },
            ReworkMarkerTag = "agent-rework-requested",
        };

        var signals = new List<ReworkSignal>
        {
            new()
            {
                PullRequestId = "1",
                Threads = ActiveThread("t1", "reviewer@example.com"),
            },
        };

        var result = await pipeline.FilterAsync(query, signals, CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task FilterAsync_LabelFetchFailure_FailsClosedPerPr()
    {
        var pipeline = CreatePipeline(new FailingPrLabelSource());

        var query = new FeedbackQuery
        {
            OpenPrs = new List<PrUnderTest>
            {
                new() { PullRequestId = "1", PullRequestUrl = "https://example.com/pr/1" },
            },
            AllowedReviewers = new HashSet<string> { "reviewer@example.com" },
            ReworkMarkerTag = "agent-rework-requested",
        };

        var signals = new List<ReworkSignal>
        {
            new()
            {
                PullRequestId = "1",
                Threads = ActiveThread("t1", "reviewer@example.com"),
            },
        };

        var result = await pipeline.FilterAsync(query, signals, CancellationToken.None);

        Assert.Empty(result);
    }

    // ── Thread-status filter ───────────────────────────────────────

    [Fact]
    public async Task FilterAsync_KeepsOnlyActiveThreads()
    {
        var pipeline = CreatePipeline(new TestPrLabelSource(
            new Dictionary<string, List<PrLabel>>
            {
                ["1"] = new()
                {
                    new() { Name = "agent-rework-requested", CreatedBy = "reviewer@example.com" },
                },
            }));

        var query = new FeedbackQuery
        {
            OpenPrs = new List<PrUnderTest>
            {
                new() { PullRequestId = "1", PullRequestUrl = "https://example.com/pr/1" },
            },
            AllowedReviewers = new HashSet<string> { "reviewer@example.com" },
            ReworkMarkerTag = "agent-rework-requested",
        };

        var signals = new List<ReworkSignal>
        {
            new()
            {
                PullRequestId = "1",
                Threads = new List<ReviewThread>
                {
                    new()
                    {
                        ThreadId = "active",
                        Status = ReviewThreadStatus.Active,
                        Comments = new List<ReviewThreadComment>
                        {
                            new() { Author = "reviewer@example.com", Body = "fix" },
                        },
                    },
                    new()
                    {
                        ThreadId = "resolved",
                        Status = ReviewThreadStatus.Resolved,
                        Comments = new List<ReviewThreadComment>
                        {
                            new() { Author = "reviewer@example.com", Body = "fix" },
                        },
                    },
                    new()
                    {
                        ThreadId = "fixed",
                        Status = ReviewThreadStatus.Fixed,
                        Comments = new List<ReviewThreadComment>
                        {
                            new() { Author = "reviewer@example.com", Body = "fix" },
                        },
                    },
                },
            },
        };

        var result = await pipeline.FilterAsync(query, signals, CancellationToken.None);

        Assert.Single(result);
        Assert.Single(result[0].Threads);
        Assert.Equal("active", result[0].Threads[0].ThreadId);
    }

    [Fact]
    public async Task FilterAsync_AllThreadsNonActive_ReturnsEmpty()
    {
        var pipeline = CreatePipeline(new TestPrLabelSource(
            new Dictionary<string, List<PrLabel>>
            {
                ["1"] = new()
                {
                    new() { Name = "agent-rework-requested", CreatedBy = "reviewer@example.com" },
                },
            }));

        var query = new FeedbackQuery
        {
            OpenPrs = new List<PrUnderTest>
            {
                new() { PullRequestId = "1", PullRequestUrl = "https://example.com/pr/1" },
            },
            AllowedReviewers = new HashSet<string> { "reviewer@example.com" },
            ReworkMarkerTag = "agent-rework-requested",
        };

        var signals = new List<ReworkSignal>
        {
            new()
            {
                PullRequestId = "1",
                Threads = new List<ReviewThread>
                {
                    new()
                    {
                        ThreadId = "resolved",
                        Status = ReviewThreadStatus.Resolved,
                        Comments = new List<ReviewThreadComment>
                        {
                            new() { Author = "reviewer@example.com", Body = "fix" },
                        },
                    },
                },
            },
        };

        var result = await pipeline.FilterAsync(query, signals, CancellationToken.None);

        Assert.Empty(result);
    }

    // ── Thread-author filter ───────────────────────────────────────

    [Fact]
    public async Task FilterAsync_KeepsThreadsWithAllowedReviewerComment()
    {
        var pipeline = CreatePipeline(new TestPrLabelSource(
            new Dictionary<string, List<PrLabel>>
            {
                ["1"] = new()
                {
                    new() { Name = "agent-rework-requested", CreatedBy = "reviewer@example.com" },
                },
            }));

        var query = new FeedbackQuery
        {
            OpenPrs = new List<PrUnderTest>
            {
                new() { PullRequestId = "1", PullRequestUrl = "https://example.com/pr/1" },
            },
            AllowedReviewers = new HashSet<string> { "reviewer@example.com" },
            ReworkMarkerTag = "agent-rework-requested",
        };

        var signals = new List<ReworkSignal>
        {
            new()
            {
                PullRequestId = "1",
                Threads = new List<ReviewThread>
                {
                    // Thread by allowed reviewer — kept
                    new()
                    {
                        ThreadId = "by-reviewer",
                        Status = ReviewThreadStatus.Active,
                        Comments = new List<ReviewThreadComment>
                        {
                            new() { Author = "reviewer@example.com", Body = "fix this" },
                        },
                    },
                    // Thread by non-allowed reviewer — dropped
                    new()
                    {
                        ThreadId = "by-other",
                        Status = ReviewThreadStatus.Active,
                        Comments = new List<ReviewThreadComment>
                        {
                            new() { Author = "other@example.com", Body = "fix this" },
                        },
                    },
                    // Thread with reply from allowed reviewer — kept
                    new()
                    {
                        ThreadId = "reply-by-reviewer",
                        Status = ReviewThreadStatus.Active,
                        Comments = new List<ReviewThreadComment>
                        {
                            new() { Author = "other@example.com", Body = "initial" },
                            new() { Author = "reviewer@example.com", Body = "yes fix", IsReply = true },
                        },
                    },
                },
            },
        };

        var result = await pipeline.FilterAsync(query, signals, CancellationToken.None);

        Assert.Single(result);
        Assert.Equal(2, result[0].Threads.Count);
        Assert.Contains(result[0].Threads, t => t.ThreadId == "by-reviewer");
        Assert.Contains(result[0].Threads, t => t.ThreadId == "reply-by-reviewer");
    }

    [Fact]
    public async Task FilterAsync_NoThreadsByAllowedReviewer_ReturnsEmpty()
    {
        var pipeline = CreatePipeline(new TestPrLabelSource(
            new Dictionary<string, List<PrLabel>>
            {
                ["1"] = new()
                {
                    new() { Name = "agent-rework-requested", CreatedBy = "reviewer@example.com" },
                },
            }));

        var query = new FeedbackQuery
        {
            OpenPrs = new List<PrUnderTest>
            {
                new() { PullRequestId = "1", PullRequestUrl = "https://example.com/pr/1" },
            },
            AllowedReviewers = new HashSet<string> { "reviewer@example.com" },
            ReworkMarkerTag = "agent-rework-requested",
        };

        var signals = new List<ReworkSignal>
        {
            new()
            {
                PullRequestId = "1",
                Threads = new List<ReviewThread>
                {
                    new()
                    {
                        ThreadId = "t1",
                        Status = ReviewThreadStatus.Active,
                        Comments = new List<ReviewThreadComment>
                        {
                            new() { Author = "other@example.com", Body = "fix this" },
                        },
                    },
                },
            },
        };

        var result = await pipeline.FilterAsync(query, signals, CancellationToken.None);

        Assert.Empty(result);
    }

    // ── Comment-content filter ─────────────────────────────────────

    [Fact]
    public async Task FilterAsync_DropsEmptyCommentThreads()
    {
        var pipeline = CreatePipeline(new TestPrLabelSource(
            new Dictionary<string, List<PrLabel>>
            {
                ["1"] = new()
                {
                    new() { Name = "agent-rework-requested", CreatedBy = "reviewer@example.com" },
                },
            }));

        var query = new FeedbackQuery
        {
            OpenPrs = new List<PrUnderTest>
            {
                new() { PullRequestId = "1", PullRequestUrl = "https://example.com/pr/1" },
            },
            AllowedReviewers = new HashSet<string> { "reviewer@example.com" },
            ReworkMarkerTag = "agent-rework-requested",
        };

        var signals = new List<ReworkSignal>
        {
            new()
            {
                PullRequestId = "1",
                Threads = new List<ReviewThread>
                {
                    // Has content — kept
                    new()
                    {
                        ThreadId = "with-content",
                        Status = ReviewThreadStatus.Active,
                        Comments = new List<ReviewThreadComment>
                        {
                            new() { Author = "reviewer@example.com", Body = "fix this" },
                        },
                    },
                    // Empty body — dropped
                    new()
                    {
                        ThreadId = "empty",
                        Status = ReviewThreadStatus.Active,
                        Comments = new List<ReviewThreadComment>
                        {
                            new() { Author = "reviewer@example.com", Body = string.Empty },
                        },
                    },
                    // Whitespace only — dropped
                    new()
                    {
                        ThreadId = "whitespace",
                        Status = ReviewThreadStatus.Active,
                        Comments = new List<ReviewThreadComment>
                        {
                            new() { Author = "reviewer@example.com", Body = "   \n\t  " },
                        },
                    },
                },
            },
        };

        var result = await pipeline.FilterAsync(query, signals, CancellationToken.None);

        Assert.Single(result);
        Assert.Single(result[0].Threads);
        Assert.Equal("with-content", result[0].Threads[0].ThreadId);
    }

    [Fact]
    public async Task FilterAsync_AllCommentsEmpty_ReturnsEmpty()
    {
        var pipeline = CreatePipeline(new TestPrLabelSource(
            new Dictionary<string, List<PrLabel>>
            {
                ["1"] = new()
                {
                    new() { Name = "agent-rework-requested", CreatedBy = "reviewer@example.com" },
                },
            }));

        var query = new FeedbackQuery
        {
            OpenPrs = new List<PrUnderTest>
            {
                new() { PullRequestId = "1", PullRequestUrl = "https://example.com/pr/1" },
            },
            AllowedReviewers = new HashSet<string> { "reviewer@example.com" },
            ReworkMarkerTag = "agent-rework-requested",
        };

        var signals = new List<ReworkSignal>
        {
            new()
            {
                PullRequestId = "1",
                Threads = new List<ReviewThread>
                {
                    new()
                    {
                        ThreadId = "t1",
                        Status = ReviewThreadStatus.Active,
                        Comments = new List<ReviewThreadComment>
                        {
                            new() { Author = "reviewer@example.com", Body = string.Empty },
                        },
                    },
                },
            },
        };

        var result = await pipeline.FilterAsync(query, signals, CancellationToken.None);

        Assert.Empty(result);
    }

    // ── Load-bearing order: all filters combined ───────────────────

    [Fact]
    public async Task FilterAsync_FullPipeline_CorrectOrder()
    {
        var pipeline = CreatePipeline(new TestPrLabelSource(
            new Dictionary<string, List<PrLabel>>
            {
                ["1"] = new()
                {
                    new() { Name = "agent-rework-requested", CreatedBy = "reviewer@example.com" },
                },
            }));

        var query = new FeedbackQuery
        {
            OpenPrs = new List<PrUnderTest>
            {
                new() { PullRequestId = "1", PullRequestUrl = "https://example.com/pr/1" },
            },
            AllowedReviewers = new HashSet<string> { "reviewer@example.com" },
            ReworkMarkerTag = "agent-rework-requested",
        };

        var signals = new List<ReworkSignal>
        {
            new()
            {
                PullRequestId = "1",
                Threads = new List<ReviewThread>
                {
                    // Active, by reviewer, has content — survives all filters
                    new()
                    {
                        ThreadId = "survives",
                        Status = ReviewThreadStatus.Active,
                        Comments = new List<ReviewThreadComment>
                        {
                            new() { Author = "reviewer@example.com", Body = "fix this" },
                        },
                    },
                    // Resolved — dropped by status filter
                    new()
                    {
                        ThreadId = "resolved",
                        Status = ReviewThreadStatus.Resolved,
                        Comments = new List<ReviewThreadComment>
                        {
                            new() { Author = "reviewer@example.com", Body = "fix" },
                        },
                    },
                    // Active but by non-reviewer — dropped by author filter
                    new()
                    {
                        ThreadId = "wrong-author",
                        Status = ReviewThreadStatus.Active,
                        Comments = new List<ReviewThreadComment>
                        {
                            new() { Author = "other@example.com", Body = "fix" },
                        },
                    },
                    // Active, by reviewer, but empty — dropped by content filter
                    new()
                    {
                        ThreadId = "empty",
                        Status = ReviewThreadStatus.Active,
                        Comments = new List<ReviewThreadComment>
                        {
                            new() { Author = "reviewer@example.com", Body = string.Empty },
                        },
                    },
                },
            },
        };

        var result = await pipeline.FilterAsync(query, signals, CancellationToken.None);

        Assert.Single(result);
        Assert.Single(result[0].Threads);
        Assert.Equal("survives", result[0].Threads[0].ThreadId);
    }

    // ── Per-PR fail-closed ─────────────────────────────────────────

    [Fact]
    public async Task FilterAsync_OnePrFailsMarker_OtherPrPasses()
    {
        var pipeline = CreatePipeline(new TestPrLabelSource(
            new Dictionary<string, List<PrLabel>>
            {
                ["1"] = new()
                {
                    new() { Name = "agent-rework-requested", CreatedBy = "reviewer@example.com" },
                },
                ["2"] = [], // No marker — fails closed
            }));

        var query = new FeedbackQuery
        {
            OpenPrs = new List<PrUnderTest>
            {
                new() { PullRequestId = "1", PullRequestUrl = "https://example.com/pr/1" },
                new() { PullRequestId = "2", PullRequestUrl = "https://example.com/pr/2" },
            },
            AllowedReviewers = new HashSet<string> { "reviewer@example.com" },
            ReworkMarkerTag = "agent-rework-requested",
        };

        var signals = new List<ReworkSignal>
        {
            new()
            {
                PullRequestId = "1",
                Threads = ActiveThread("t1", "reviewer@example.com"),
            },
            new()
            {
                PullRequestId = "2",
                Threads = ActiveThread("t2", "reviewer@example.com"),
            },
        };

        var result = await pipeline.FilterAsync(query, signals, CancellationToken.None);

        Assert.Single(result);
        Assert.Equal("1", result[0].PullRequestId);
    }

    // ── Timestamps rebuilt from surviving threads ──────────────────

    [Fact]
    public async Task FilterAsync_RebuildsTimestampsFromSurvivingThreads()
    {
        var t1 = new DateTimeOffset(2025, 6, 1, 10, 0, 0, TimeSpan.Zero);
        var t2 = new DateTimeOffset(2025, 6, 1, 12, 0, 0, TimeSpan.Zero);

        var pipeline = CreatePipeline(new TestPrLabelSource(
            new Dictionary<string, List<PrLabel>>
            {
                ["1"] = new()
                {
                    new() { Name = "agent-rework-requested", CreatedBy = "reviewer@example.com" },
                },
            }));

        var query = new FeedbackQuery
        {
            OpenPrs = new List<PrUnderTest>
            {
                new() { PullRequestId = "1", PullRequestUrl = "https://example.com/pr/1" },
            },
            AllowedReviewers = new HashSet<string> { "reviewer@example.com" },
            ReworkMarkerTag = "agent-rework-requested",
        };

        var signals = new List<ReworkSignal>
        {
            new()
            {
                PullRequestId = "1",
                Threads = new List<ReviewThread>
                {
                    // Survives — comment at t2
                    new()
                    {
                        ThreadId = "survives",
                        Status = ReviewThreadStatus.Active,
                        Comments = new List<ReviewThreadComment>
                        {
                            new() { Author = "reviewer@example.com", Body = "fix", CreatedAt = t2 },
                        },
                    },
                    // Dropped by status — had earlier comment at t1
                    new()
                    {
                        ThreadId = "resolved",
                        Status = ReviewThreadStatus.Resolved,
                        Comments = new List<ReviewThreadComment>
                        {
                            new() { Author = "reviewer@example.com", Body = "fix", CreatedAt = t1 },
                        },
                    },
                },
                FirstQualifyingCommentAt = t1, // Original earliest (from dropped thread)
                LastQualifyingCommentAt = t2,
            },
        };

        var result = await pipeline.FilterAsync(query, signals, CancellationToken.None);

        Assert.Single(result);
        // Timestamps should reflect only surviving threads
        Assert.Equal(t2, result[0].FirstQualifyingCommentAt);
        Assert.Equal(t2, result[0].LastQualifyingCommentAt);
    }

    // ── Edge cases ─────────────────────────────────────────────────

    [Fact]
    public async Task FilterAsync_EmptySignals_ReturnsEmpty()
    {
        var pipeline = CreatePipeline(new TestPrLabelSource([]));
        var query = new FeedbackQuery
        {
            OpenPrs = [],
            AllowedReviewers = new HashSet<string> { "reviewer@example.com" },
        };

        var result = await pipeline.FilterAsync(query, Array.Empty<ReworkSignal>(), CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public void Pipeline_ImplementsIDisposable()
    {
        Assert.True(typeof(IDisposable).IsAssignableFrom(typeof(ReviewFeedbackFilterPipeline)));
    }

    // ── Helpers ────────────────────────────────────────────────────

    private static List<ReviewThread> ActiveThread(string threadId, string author)
    {
        return new List<ReviewThread>
        {
            new()
            {
                ThreadId = threadId,
                Status = ReviewThreadStatus.Active,
                Comments = new List<ReviewThreadComment>
                {
                    new() { Author = author, Body = "fix this" },
                },
            },
        };
    }

    // ── Test doubles ───────────────────────────────────────────────

    private sealed class TestPrLabelSource : IPrLabelSource
    {
        private readonly Dictionary<string, IReadOnlyList<PrLabel>> _labels;

        public TestPrLabelSource(Dictionary<string, List<PrLabel>> labels)
        {
            _labels = labels.ToDictionary(
                kvp => kvp.Key,
                kvp => (IReadOnlyList<PrLabel>)kvp.Value,
                StringComparer.Ordinal);
        }

        public Task<IReadOnlyList<PrLabel>> GetLabelsAsync(
            PrUnderTest pr,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<PrLabel>>(
                _labels.TryGetValue(pr.PullRequestId, out var labels) ? labels : Array.Empty<PrLabel>());
        }
    }

    private sealed class FailingPrLabelSource : IPrLabelSource
    {
        public Task<IReadOnlyList<PrLabel>> GetLabelsAsync(
            PrUnderTest pr,
            CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("Simulated label fetch failure");
        }
    }
}

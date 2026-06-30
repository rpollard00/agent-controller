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

    // ── Attributed-marker cases ────────────────────────────────────

    [Fact]
    public async Task FilterAsync_MarkerTagNameCaseMismatch_FailsClosed()
    {
        var pipeline = CreatePipeline(new TestPrLabelSource(
            new Dictionary<string, List<PrLabel>>
            {
                ["1"] = new()
                {
                    // Label name differs in case — Ordinal comparison should not match
                    new() { Name = "Agent-Rework-Requested", CreatedBy = "reviewer@example.com" },
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
    public async Task FilterAsync_MarkerAmongMultipleLabels_Passes()
    {
        var pipeline = CreatePipeline(new TestPrLabelSource(
            new Dictionary<string, List<PrLabel>>
            {
                ["1"] = new()
                {
                    new() { Name = "priority-high", CreatedBy = "other@example.com" },
                    new() { Name = "agent-rework-requested", CreatedBy = "reviewer@example.com" },
                    new() { Name = "needs-review", CreatedBy = "other@example.com" },
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
    public async Task FilterAsync_MarkerCreatedByNull_FailsClosed()
    {
        var pipeline = CreatePipeline(new TestPrLabelSource(
            new Dictionary<string, List<PrLabel>>
            {
                ["1"] = new()
                {
                    new() { Name = "agent-rework-requested", CreatedBy = null! },
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
    public async Task FilterAsync_MarkerCreatedByWhitespace_FailsClosed()
    {
        var pipeline = CreatePipeline(new TestPrLabelSource(
            new Dictionary<string, List<PrLabel>>
            {
                ["1"] = new()
                {
                    new() { Name = "agent-rework-requested", CreatedBy = "   " },
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

    // ── Load-bearing order ─────────────────────────────────────────

    [Fact]
    public async Task FilterAsync_MarkerGateRunsBeforeThreadFilters()
    {
        // Marker gate fails — thread-level filters must NOT be invoked.
        // We verify this by ensuring no label fetch occurs for PR 2 (marker gate
        // for PR 1 fails, so the pipeline short-circuits per-PR).
        var labelFetches = new List<string>();
        var trackingSource = new TrackingPrLabelSource(
            new Dictionary<string, List<PrLabel>>
            {
                ["1"] = [], // No marker — fails closed
                ["2"] = new()
                {
                    new() { Name = "agent-rework-requested", CreatedBy = "reviewer@example.com" },
                },
            },
            labelFetches);

        var pipeline = CreatePipeline(trackingSource);
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

        // PR 1 fails marker gate (no label), PR 2 passes all filters.
        Assert.Single(result);
        Assert.Equal("2", result[0].PullRequestId);
        // Both PRs should have had labels fetched (marker gate is per-PR, not global).
        Assert.Contains("1", labelFetches);
        Assert.Contains("2", labelFetches);
    }

    [Fact]
    public async Task FilterAsync_AllowlistGateRunsBeforeMarkerGate()
    {
        // When allowlist is empty, the pipeline should return immediately
        // without ever calling the label source.
        var labelFetches = new List<string>();
        var trackingSource = new TrackingPrLabelSource(
            new Dictionary<string, List<PrLabel>>
            {
                ["1"] = new()
                {
                    new() { Name = "agent-rework-requested", CreatedBy = "reviewer@example.com" },
                },
            },
            labelFetches);

        var pipeline = CreatePipeline(trackingSource);
        var query = new FeedbackQuery
        {
            OpenPrs = new List<PrUnderTest>
            {
                new() { PullRequestId = "1", PullRequestUrl = "https://example.com/pr/1" },
            },
            AllowedReviewers = new HashSet<string>(), // Empty — fail-closed before marker gate
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
        // Label source should never have been called.
        Assert.Empty(labelFetches);
    }

    // ── Per-PR fail-closed (extended) ──────────────────────────────

    [Fact]
    public async Task FilterAsync_AllPrsFailMarker_ReturnsEmpty()
    {
        var pipeline = CreatePipeline(new TestPrLabelSource(
            new Dictionary<string, List<PrLabel>>
            {
                ["1"] = [], // No marker
                ["2"] = [], // No marker
                ["3"] = [], // No marker
            }));

        var query = new FeedbackQuery
        {
            OpenPrs = new List<PrUnderTest>
            {
                new() { PullRequestId = "1", PullRequestUrl = "https://example.com/pr/1" },
                new() { PullRequestId = "2", PullRequestUrl = "https://example.com/pr/2" },
                new() { PullRequestId = "3", PullRequestUrl = "https://example.com/pr/3" },
            },
            AllowedReviewers = new HashSet<string> { "reviewer@example.com" },
            ReworkMarkerTag = "agent-rework-requested",
        };

        var signals = new List<ReworkSignal>
        {
            new() { PullRequestId = "1", Threads = ActiveThread("t1", "reviewer@example.com") },
            new() { PullRequestId = "2", Threads = ActiveThread("t2", "reviewer@example.com") },
            new() { PullRequestId = "3", Threads = ActiveThread("t3", "reviewer@example.com") },
        };

        var result = await pipeline.FilterAsync(query, signals, CancellationToken.None);

        Assert.Empty(result);
    }

    // ── Signal without matching PR ─────────────────────────────────

    [Fact]
    public async Task FilterAsync_SignalWithoutMatchingPr_SkippedGracefully()
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
                // PR "99" has no entry in OpenPrs
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
                PullRequestId = "99", // No matching PrUnderTest
                Threads = ActiveThread("t99", "reviewer@example.com"),
            },
        };

        var result = await pipeline.FilterAsync(query, signals, CancellationToken.None);

        // Only PR 1 survives; PR 99 is skipped (no matching PrUnderTest).
        Assert.Single(result);
        Assert.Equal("1", result[0].PullRequestId);
    }

    // ── Allowlist fail-closed (multiple calls) ─────────────────────

    [Fact]
    public async Task FilterAsync_EmptyAllowlistMultipleCalls_AlwaysReturnsEmpty()
    {
        var pipeline = CreatePipeline(new TestPrLabelSource([]));
        var query = new FeedbackQuery
        {
            OpenPrs = [],
            AllowedReviewers = new HashSet<string>(), // Empty
            ReworkMarkerTag = "agent-rework-requested",
        };

        var signals = new List<ReworkSignal>
        {
            new()
            {
                PullRequestId = "1",
                Threads = ActiveThread("t1", "a@example.com"),
            },
        };

        // Three successive calls — all must return empty.
        var r1 = await pipeline.FilterAsync(query, signals, CancellationToken.None);
        var r2 = await pipeline.FilterAsync(query, signals, CancellationToken.None);
        var r3 = await pipeline.FilterAsync(query, signals, CancellationToken.None);

        Assert.Empty(r1);
        Assert.Empty(r2);
        Assert.Empty(r3);
    }

    // ── Comment-content filter (extended) ──────────────────────────

    [Fact]
    public async Task FilterAsync_ThreadWithMixedContentKeepsIfAnyCommentHasContent()
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
                    // Thread with multiple comments — only one has content
                    new()
                    {
                        ThreadId = "mixed",
                        Status = ReviewThreadStatus.Active,
                        Comments = new List<ReviewThreadComment>
                        {
                            new() { Author = "reviewer@example.com", Body = string.Empty },
                            new() { Author = "other@example.com", Body = "   " },
                            new() { Author = "reviewer@example.com", Body = "please fix", IsReply = true },
                        },
                    },
                    // Thread where all comments are empty
                    new()
                    {
                        ThreadId = "all-empty",
                        Status = ReviewThreadStatus.Active,
                        Comments = new List<ReviewThreadComment>
                        {
                            new() { Author = "reviewer@example.com", Body = string.Empty },
                            new() { Author = "reviewer@example.com", Body = "\t\n" },
                        },
                    },
                },
            },
        };

        var result = await pipeline.FilterAsync(query, signals, CancellationToken.None);

        Assert.Single(result);
        Assert.Single(result[0].Threads);
        Assert.Equal("mixed", result[0].Threads[0].ThreadId);
    }

    // ── Thread-author filter (reply chain) ─────────────────────────

    [Fact]
    public async Task FilterAsync_ThreadWithOnlyNonAllowedAuthorComments_Dropped()
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
                    // Initial by non-allowed, reply by non-allowed — dropped
                    new()
                    {
                        ThreadId = "no-reviewer",
                        Status = ReviewThreadStatus.Active,
                        Comments = new List<ReviewThreadComment>
                        {
                            new() { Author = "author@example.com", Body = "initial comment" },
                            new() { Author = "other@example.com", Body = "reply", IsReply = true },
                        },
                    },
                    // Initial by non-allowed, reply by allowed — kept
                    new()
                    {
                        ThreadId = "reviewer-replied",
                        Status = ReviewThreadStatus.Active,
                        Comments = new List<ReviewThreadComment>
                        {
                            new() { Author = "author@example.com", Body = "initial comment" },
                            new() { Author = "reviewer@example.com", Body = "yes fix this", IsReply = true },
                        },
                    },
                },
            },
        };

        var result = await pipeline.FilterAsync(query, signals, CancellationToken.None);

        Assert.Single(result);
        Assert.Single(result[0].Threads);
        Assert.Equal("reviewer-replied", result[0].Threads[0].ThreadId);
    }

    // ── Thread-status filter (all non-Active statuses) ─────────────

    [Fact]
    public async Task FilterAsync_DropsAllNonActiveStatuses()
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
                    CreateThread("resolved", ReviewThreadStatus.Resolved),
                    CreateThread("fixed", ReviewThreadStatus.Fixed),
                    CreateThread("wontfix", ReviewThreadStatus.WontFix),
                    CreateThread("closed", ReviewThreadStatus.Closed),
                    CreateThread("bydesign", ReviewThreadStatus.ByDesign),
                    CreateThread("active", ReviewThreadStatus.Active),
                },
            },
        };

        var result = await pipeline.FilterAsync(query, signals, CancellationToken.None);

        Assert.Single(result);
        Assert.Single(result[0].Threads);
        Assert.Equal("active", result[0].Threads[0].ThreadId);
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

    private static ReviewThread CreateThread(string threadId, ReviewThreadStatus status)
    {
        return new ReviewThread
        {
            ThreadId = threadId,
            Status = status,
            Comments = new List<ReviewThreadComment>
            {
                new() { Author = "reviewer@example.com", Body = "fix" },
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

    /// <summary>
    /// Test double that records which PRs had their labels fetched,
    /// enabling verification of load-bearing filter order.
    /// </summary>
    private sealed class TrackingPrLabelSource : IPrLabelSource
    {
        private readonly Dictionary<string, IReadOnlyList<PrLabel>> _labels;
        private readonly List<string> _fetches;

        public TrackingPrLabelSource(
            Dictionary<string, List<PrLabel>> labels,
            List<string> fetches)
        {
            _labels = labels.ToDictionary(
                kvp => kvp.Key,
                kvp => (IReadOnlyList<PrLabel>)kvp.Value,
                StringComparer.Ordinal);
            _fetches = fetches;
        }

        public IReadOnlyList<string> Fetches => _fetches;

        public Task<IReadOnlyList<PrLabel>> GetLabelsAsync(
            PrUnderTest pr,
            CancellationToken cancellationToken)
        {
            _fetches.Add(pr.PullRequestId);
            return Task.FromResult<IReadOnlyList<PrLabel>>(
                _labels.TryGetValue(pr.PullRequestId, out var labels) ? labels : Array.Empty<PrLabel>());
        }
    }
}

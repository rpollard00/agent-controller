using AgentController.Application;
using AgentController.Domain;
using AgentController.Infrastructure;
using AgentController.Infrastructure.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AgentController.Infrastructure.Tests;

public class LocalFeedbackSourceTests
{
    // ── Smoke / Interface tests ──────────────────────────────────────

    [Fact]
    public void LocalFeedbackSource_ImplementsInterface()
    {
        var type = typeof(LocalFeedbackSource);
        Assert.True(
            typeof(IFeedbackSource).IsAssignableFrom(type),
            "LocalFeedbackSource should implement IFeedbackSource");
    }

    [Fact]
    public void LocalFeedbackSource_ImplementsIDisposable()
    {
        var type = typeof(LocalFeedbackSource);
        Assert.True(
            typeof(IDisposable).IsAssignableFrom(type),
            "LocalFeedbackSource should implement IDisposable");
    }

    [Fact]
    public void LocalFeedbackOptions_SectionName_IsCorrect()
    {
        Assert.Equal("localFeedback", LocalFeedbackOptions.SectionName);
    }

    [Fact]
    public void LocalFeedbackSignalDefinition_HasExpectedDefaults()
    {
        var def = new LocalFeedbackSignalDefinition();

        Assert.Equal(string.Empty, def.PullRequestId);
        Assert.Null(def.OriginatingRunId);
        Assert.Empty(def.Threads);
    }

    [Fact]
    public void LocalFeedbackThreadDefinition_HasExpectedDefaults()
    {
        var def = new LocalFeedbackThreadDefinition();

        Assert.Equal(string.Empty, def.ThreadId);
        Assert.Equal(string.Empty, def.Author);
        Assert.Null(def.CreatedAt);
        Assert.Equal("Active", def.Status);
        Assert.Null(def.FilePath);
        Assert.Null(def.StartLine);
        Assert.Null(def.EndLine);
        Assert.False(def.IsFileLevel);
        Assert.Empty(def.Comments);
    }

    [Fact]
    public void LocalFeedbackCommentDefinition_HasExpectedDefaults()
    {
        var def = new LocalFeedbackCommentDefinition();

        Assert.Equal(string.Empty, def.Author);
        Assert.Equal(string.Empty, def.Body);
        Assert.Null(def.CreatedAt);
        Assert.False(def.IsReply);
    }

    // ── Options binding tests ────────────────────────────────────────

    [Fact]
    public void LocalFeedbackOptions_BindsSingleSignal()
    {
        var config = BuildConfiguration(new Dictionary<string, string?>
        {
            ["localFeedback:signals:0:pullRequestId"] = "42",
            ["localFeedback:signals:0:originatingRunId"] = "run-abc-123",
            ["localFeedback:signals:0:threads:0:threadId"] = "thread-1",
            ["localFeedback:signals:0:threads:0:author"] = "reviewer@example.com",
            ["localFeedback:signals:0:threads:0:status"] = "Active",
            ["localFeedback:signals:0:threads:0:filePath"] = "src/Service.cs",
            ["localFeedback:signals:0:threads:0:startLine"] = "10",
            ["localFeedback:signals:0:threads:0:endLine"] = "15",
            ["localFeedback:signals:0:threads:0:isFileLevel"] = "false",
            ["localFeedback:signals:0:threads:0:comments:0:author"] = "reviewer@example.com",
            ["localFeedback:signals:0:threads:0:comments:0:body"] = "This needs fixing",
            ["localFeedback:signals:0:threads:0:comments:0:createdAt"] = "2025-06-01T10:00:00Z",
            ["localFeedback:signals:0:threads:0:comments:0:isReply"] = "false",
        });

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services
            .AddOptions<LocalFeedbackOptions>()
            .Bind(config.GetSection(LocalFeedbackOptions.SectionName));

        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<LocalFeedbackOptions>>().Value;

        Assert.NotNull(options);
        Assert.Single(options.Signals);

        var signal = options.Signals[0];
        Assert.Equal("42", signal.PullRequestId);
        Assert.Equal("run-abc-123", signal.OriginatingRunId);
        Assert.Single(signal.Threads);

        var thread = signal.Threads[0];
        Assert.Equal("thread-1", thread.ThreadId);
        Assert.Equal("reviewer@example.com", thread.Author);
        Assert.Equal("Active", thread.Status);
        Assert.Equal("src/Service.cs", thread.FilePath);
        Assert.Equal(10, thread.StartLine);
        Assert.Equal(15, thread.EndLine);
        Assert.False(thread.IsFileLevel);
        Assert.Single(thread.Comments);

        var comment = thread.Comments[0];
        Assert.Equal("reviewer@example.com", comment.Author);
        Assert.Equal("This needs fixing", comment.Body);
        Assert.Equal("2025-06-01T10:00:00Z", comment.CreatedAt);
        Assert.False(comment.IsReply);
    }

    [Fact]
    public void LocalFeedbackOptions_EmptySignalsBindsAsEmptyList()
    {
        var config = BuildConfiguration(new Dictionary<string, string?>
        {
            // No localFeedback:signals keys
        });

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services
            .AddOptions<LocalFeedbackOptions>()
            .Bind(config.GetSection(LocalFeedbackOptions.SectionName));

        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<LocalFeedbackOptions>>().Value;

        Assert.NotNull(options);
        Assert.Empty(options.Signals);
    }

    [Fact]
    public void LocalFeedbackOptions_BindsMultipleSignals()
    {
        var config = BuildConfiguration(new Dictionary<string, string?>
        {
            ["localFeedback:signals:0:pullRequestId"] = "1",
            ["localFeedback:signals:0:threads:0:threadId"] = "t1",
            ["localFeedback:signals:0:threads:0:comments:0:author"] = "a@example.com",
            ["localFeedback:signals:0:threads:0:comments:0:body"] = "c1",
            ["localFeedback:signals:1:pullRequestId"] = "2",
            ["localFeedback:signals:1:threads:0:threadId"] = "t2",
            ["localFeedback:signals:1:threads:0:comments:0:author"] = "b@example.com",
            ["localFeedback:signals:1:threads:0:comments:0:body"] = "c2",
        });

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services
            .AddOptions<LocalFeedbackOptions>()
            .Bind(config.GetSection(LocalFeedbackOptions.SectionName));

        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<LocalFeedbackOptions>>().Value;

        Assert.Equal(2, options.Signals.Count);
        Assert.Equal("1", options.Signals[0].PullRequestId);
        Assert.Equal("2", options.Signals[1].PullRequestId);
    }

    // ── PollAsync behavior tests ─────────────────────────────────────

    [Fact]
    public async Task PollAsync_WithMatchingPR_ReturnsSignal()
    {
        var config = BuildConfiguration(new Dictionary<string, string?>
        {
            ["localFeedback:signals:0:pullRequestId"] = "42",
            ["localFeedback:signals:0:originatingRunId"] = "run-abc",
            ["localFeedback:signals:0:threads:0:threadId"] = "thread-1",
            ["localFeedback:signals:0:threads:0:author"] = "reviewer@example.com",
            ["localFeedback:signals:0:threads:0:status"] = "Active",
            ["localFeedback:signals:0:threads:0:filePath"] = "src/Service.cs",
            ["localFeedback:signals:0:threads:0:startLine"] = "10",
            ["localFeedback:signals:0:threads:0:endLine"] = "15",
            ["localFeedback:signals:0:threads:0:comments:0:author"] = "reviewer@example.com",
            ["localFeedback:signals:0:threads:0:comments:0:body"] = "Fix this please",
            ["localFeedback:signals:0:threads:0:comments:0:createdAt"] = "2025-06-01T10:00:00Z",
        });

        var optionsMonitor = BuildOptionsMonitor(config);
        var source = new LocalFeedbackSource(optionsMonitor, NullLogger<LocalFeedbackSource>.Instance);

        try
        {
            var query = new FeedbackQuery
            {
                OpenPrs = new List<PrUnderTest>
                {
                    new()
                    {
                        OriginatingRunId = "run-abc",
                        WorkItemId = "WI-1",
                        RepoKey = "org/repo",
                        PullRequestUrl = "https://example.com/pr/42",
                        PullRequestId = "42",
                        BranchName = "feature/branch",
                    },
                },
            };

            var signals = await source.PollAsync(query, CancellationToken.None);

            Assert.Single(signals);
            var signal = signals[0];
            Assert.Equal("run-abc", signal.OriginatingRunId);
            Assert.Equal("42", signal.PullRequestId);
            Assert.Single(signal.Threads);

            var thread = signal.Threads[0];
            Assert.Equal("thread-1", thread.ThreadId);
            Assert.Equal("reviewer@example.com", thread.Author);
            Assert.Equal(ReviewThreadStatus.Active, thread.Status);
            Assert.Equal("src/Service.cs", thread.FilePath);
            Assert.Equal(10, thread.StartLine);
            Assert.Equal(15, thread.EndLine);
            Assert.False(thread.IsFileLevel);
            Assert.Single(thread.Comments);
        }
        finally
        {
            source.Dispose();
        }
    }

    [Fact]
    public async Task PollAsync_WithNoMatchingPR_ReturnsEmpty()
    {
        var config = BuildConfiguration(new Dictionary<string, string?>
        {
            ["localFeedback:signals:0:pullRequestId"] = "42",
            ["localFeedback:signals:0:threads:0:threadId"] = "thread-1",
            ["localFeedback:signals:0:threads:0:comments:0:author"] = "a@example.com",
            ["localFeedback:signals:0:threads:0:comments:0:body"] = "c",
        });

        var optionsMonitor = BuildOptionsMonitor(config);
        var source = new LocalFeedbackSource(optionsMonitor, NullLogger<LocalFeedbackSource>.Instance);

        try
        {
            var query = new FeedbackQuery
            {
                OpenPrs = new List<PrUnderTest>
                {
                    new()
                    {
                        PullRequestId = "99", // Does not match configured "42"
                    },
                },
            };

            var signals = await source.PollAsync(query, CancellationToken.None);

            Assert.Empty(signals);
        }
        finally
        {
            source.Dispose();
        }
    }

    [Fact]
    public async Task PollAsync_WithEmptyOpenPrs_ReturnsEmpty()
    {
        var config = BuildConfiguration(new Dictionary<string, string?>
        {
            ["localFeedback:signals:0:pullRequestId"] = "42",
            ["localFeedback:signals:0:threads:0:threadId"] = "thread-1",
            ["localFeedback:signals:0:threads:0:comments:0:author"] = "a@example.com",
            ["localFeedback:signals:0:threads:0:comments:0:body"] = "c",
        });

        var optionsMonitor = BuildOptionsMonitor(config);
        var source = new LocalFeedbackSource(optionsMonitor, NullLogger<LocalFeedbackSource>.Instance);

        try
        {
            var query = new FeedbackQuery { OpenPrs = [] };

            var signals = await source.PollAsync(query, CancellationToken.None);

            Assert.Empty(signals);
        }
        finally
        {
            source.Dispose();
        }
    }

    [Fact]
    public async Task PollAsync_MatchesMultiplePRs()
    {
        var config = BuildConfiguration(new Dictionary<string, string?>
        {
            ["localFeedback:signals:0:pullRequestId"] = "1",
            ["localFeedback:signals:0:threads:0:threadId"] = "t1",
            ["localFeedback:signals:0:threads:0:comments:0:author"] = "a@example.com",
            ["localFeedback:signals:0:threads:0:comments:0:body"] = "c1",
            ["localFeedback:signals:1:pullRequestId"] = "2",
            ["localFeedback:signals:1:threads:0:threadId"] = "t2",
            ["localFeedback:signals:1:threads:0:comments:0:author"] = "b@example.com",
            ["localFeedback:signals:1:threads:0:comments:0:body"] = "c2",
        });

        var optionsMonitor = BuildOptionsMonitor(config);
        var source = new LocalFeedbackSource(optionsMonitor, NullLogger<LocalFeedbackSource>.Instance);

        try
        {
            var query = new FeedbackQuery
            {
                OpenPrs = new List<PrUnderTest>
                {
                    new() { PullRequestId = "1" },
                    new() { PullRequestId = "2" },
                    new() { PullRequestId = "3" }, // No match
                },
            };

            var signals = await source.PollAsync(query, CancellationToken.None);

            Assert.Equal(2, signals.Count);
            Assert.Equal("1", signals[0].PullRequestId);
            Assert.Equal("2", signals[1].PullRequestId);
        }
        finally
        {
            source.Dispose();
        }
    }

    [Fact]
    public async Task PollAsync_DerivesOriginatingRunId_WhenNotProvided()
    {
        var config = BuildConfiguration(new Dictionary<string, string?>
        {
            ["localFeedback:signals:0:pullRequestId"] = "42",
            // No originatingRunId — should derive "local-run-42"
            ["localFeedback:signals:0:threads:0:threadId"] = "thread-1",
            ["localFeedback:signals:0:threads:0:comments:0:author"] = "a@example.com",
            ["localFeedback:signals:0:threads:0:comments:0:body"] = "c",
        });

        var optionsMonitor = BuildOptionsMonitor(config);
        var source = new LocalFeedbackSource(optionsMonitor, NullLogger<LocalFeedbackSource>.Instance);

        try
        {
            var query = new FeedbackQuery
            {
                OpenPrs = new List<PrUnderTest>
                {
                    new() { PullRequestId = "42" },
                },
            };

            var signals = await source.PollAsync(query, CancellationToken.None);

            Assert.Single(signals);
            Assert.Equal("local-run-42", signals[0].OriginatingRunId);
        }
        finally
        {
            source.Dispose();
        }
    }

    [Fact]
    public async Task PollAsync_ParsesThreadStatus_Correctly()
    {
        var config = BuildConfiguration(new Dictionary<string, string?>
        {
            ["localFeedback:signals:0:pullRequestId"] = "1",
            ["localFeedback:signals:0:threads:0:threadId"] = "t-resolved",
            ["localFeedback:signals:0:threads:0:status"] = "Resolved",
            ["localFeedback:signals:0:threads:0:comments:0:author"] = "a@example.com",
            ["localFeedback:signals:0:threads:0:comments:0:body"] = "c",
            ["localFeedback:signals:0:threads:1:threadId"] = "t-fixed",
            ["localFeedback:signals:0:threads:1:status"] = "Fixed",
            ["localFeedback:signals:0:threads:1:comments:0:author"] = "a@example.com",
            ["localFeedback:signals:0:threads:1:comments:0:body"] = "c",
            ["localFeedback:signals:0:threads:2:threadId"] = "t-wontfix",
            ["localFeedback:signals:0:threads:2:status"] = "WontFix",
            ["localFeedback:signals:0:threads:2:comments:0:author"] = "a@example.com",
            ["localFeedback:signals:0:threads:2:comments:0:body"] = "c",
            ["localFeedback:signals:0:threads:3:threadId"] = "t-closed",
            ["localFeedback:signals:0:threads:3:status"] = "Closed",
            ["localFeedback:signals:0:threads:3:comments:0:author"] = "a@example.com",
            ["localFeedback:signals:0:threads:3:comments:0:body"] = "c",
            ["localFeedback:signals:0:threads:4:threadId"] = "t-bydesign",
            ["localFeedback:signals:0:threads:4:status"] = "ByDesign",
            ["localFeedback:signals:0:threads:4:comments:0:author"] = "a@example.com",
            ["localFeedback:signals:0:threads:4:comments:0:body"] = "c",
        });

        var optionsMonitor = BuildOptionsMonitor(config);
        var source = new LocalFeedbackSource(optionsMonitor, NullLogger<LocalFeedbackSource>.Instance);

        try
        {
            var query = new FeedbackQuery
            {
                OpenPrs = new List<PrUnderTest>
                {
                    new() { PullRequestId = "1" },
                },
            };

            var signals = await source.PollAsync(query, CancellationToken.None);

            Assert.Single(signals);
            var threads = signals[0].Threads;
            Assert.Equal(5, threads.Count);
            Assert.Equal(ReviewThreadStatus.Resolved, threads[0].Status);
            Assert.Equal(ReviewThreadStatus.Fixed, threads[1].Status);
            Assert.Equal(ReviewThreadStatus.WontFix, threads[2].Status);
            Assert.Equal(ReviewThreadStatus.Closed, threads[3].Status);
            Assert.Equal(ReviewThreadStatus.ByDesign, threads[4].Status);
        }
        finally
        {
            source.Dispose();
        }
    }

    [Fact]
    public async Task PollAsync_ParsesCreatedAt_FromConfig()
    {
        var config = BuildConfiguration(new Dictionary<string, string?>
        {
            ["localFeedback:signals:0:pullRequestId"] = "1",
            ["localFeedback:signals:0:threads:0:threadId"] = "t1",
            ["localFeedback:signals:0:threads:0:createdAt"] = "2025-06-01T10:00:00Z",
            ["localFeedback:signals:0:threads:0:comments:0:author"] = "a@example.com",
            ["localFeedback:signals:0:threads:0:comments:0:body"] = "c",
            ["localFeedback:signals:0:threads:0:comments:0:createdAt"] = "2025-06-01T10:05:00Z",
        });

        var optionsMonitor = BuildOptionsMonitor(config);
        var source = new LocalFeedbackSource(optionsMonitor, NullLogger<LocalFeedbackSource>.Instance);

        try
        {
            var query = new FeedbackQuery
            {
                OpenPrs = new List<PrUnderTest>
                {
                    new() { PullRequestId = "1" },
                },
            };

            var signals = await source.PollAsync(query, CancellationToken.None);

            Assert.Single(signals);
            var signal = signals[0];
            var thread = signal.Threads[0];

            Assert.Equal(new DateTimeOffset(2025, 6, 1, 10, 0, 0, TimeSpan.Zero), thread.CreatedAt);
            Assert.Equal(new DateTimeOffset(2025, 6, 1, 10, 5, 0, TimeSpan.Zero), signal.FirstQualifyingCommentAt);
            Assert.Equal(new DateTimeOffset(2025, 6, 1, 10, 5, 0, TimeSpan.Zero), signal.LastQualifyingCommentAt);
        }
        finally
        {
            source.Dispose();
        }
    }

    [Fact]
    public async Task PollAsync_ComputesTimestamps_FromMultipleComments()
    {
        var config = BuildConfiguration(new Dictionary<string, string?>
        {
            ["localFeedback:signals:0:pullRequestId"] = "1",
            ["localFeedback:signals:0:threads:0:threadId"] = "t1",
            ["localFeedback:signals:0:threads:0:comments:0:author"] = "a@example.com",
            ["localFeedback:signals:0:threads:0:comments:0:body"] = "first comment",
            ["localFeedback:signals:0:threads:0:comments:0:createdAt"] = "2025-06-01T10:00:00Z",
            ["localFeedback:signals:0:threads:0:comments:1:author"] = "b@example.com",
            ["localFeedback:signals:0:threads:0:comments:1:body"] = "reply",
            ["localFeedback:signals:0:threads:0:comments:1:createdAt"] = "2025-06-01T12:00:00Z",
            ["localFeedback:signals:0:threads:0:comments:1:isReply"] = "true",
        });

        var optionsMonitor = BuildOptionsMonitor(config);
        var source = new LocalFeedbackSource(optionsMonitor, NullLogger<LocalFeedbackSource>.Instance);

        try
        {
            var query = new FeedbackQuery
            {
                OpenPrs = new List<PrUnderTest>
                {
                    new() { PullRequestId = "1" },
                },
            };

            var signals = await source.PollAsync(query, CancellationToken.None);

            Assert.Single(signals);
            var signal = signals[0];

            Assert.Equal(new DateTimeOffset(2025, 6, 1, 10, 0, 0, TimeSpan.Zero), signal.FirstQualifyingCommentAt);
            Assert.Equal(new DateTimeOffset(2025, 6, 1, 12, 0, 0, TimeSpan.Zero), signal.LastQualifyingCommentAt);
        }
        finally
        {
            source.Dispose();
        }
    }

    [Fact]
    public async Task PollAsync_SkipsSignalWithEmptyPullRequestId()
    {
        var config = BuildConfiguration(new Dictionary<string, string?>
        {
            // Signal 0: missing pullRequestId (skipped)
            ["localFeedback:signals:0:originatingRunId"] = "run-bad",
            ["localFeedback:signals:0:threads:0:threadId"] = "t1",
            ["localFeedback:signals:0:threads:0:comments:0:author"] = "a@example.com",
            ["localFeedback:signals:0:threads:0:comments:0:body"] = "c",
            // Signal 1: valid
            ["localFeedback:signals:1:pullRequestId"] = "42",
            ["localFeedback:signals:1:threads:0:threadId"] = "t2",
            ["localFeedback:signals:1:threads:0:comments:0:author"] = "a@example.com",
            ["localFeedback:signals:1:threads:0:comments:0:body"] = "c",
        });

        var optionsMonitor = BuildOptionsMonitor(config);
        var source = new LocalFeedbackSource(optionsMonitor, NullLogger<LocalFeedbackSource>.Instance);

        try
        {
            var query = new FeedbackQuery
            {
                OpenPrs = new List<PrUnderTest>
                {
                    new() { PullRequestId = "42" },
                },
            };

            var signals = await source.PollAsync(query, CancellationToken.None);

            // Only the valid signal should be returned
            Assert.Single(signals);
            Assert.Equal("42", signals[0].PullRequestId);
        }
        finally
        {
            source.Dispose();
        }
    }

    [Fact]
    public async Task PollAsync_SkipsSignalWithNoThreads()
    {
        var config = BuildConfiguration(new Dictionary<string, string?>
        {
            ["localFeedback:signals:0:pullRequestId"] = "42",
            // No threads defined — should be skipped
        });

        var optionsMonitor = BuildOptionsMonitor(config);
        var source = new LocalFeedbackSource(optionsMonitor, NullLogger<LocalFeedbackSource>.Instance);

        try
        {
            var query = new FeedbackQuery
            {
                OpenPrs = new List<PrUnderTest>
                {
                    new() { PullRequestId = "42" },
                },
            };

            var signals = await source.PollAsync(query, CancellationToken.None);

            Assert.Empty(signals);
        }
        finally
        {
            source.Dispose();
        }
    }

    [Fact]
    public async Task PollAsync_SkipsThreadWithEmptyThreadId()
    {
        var config = BuildConfiguration(new Dictionary<string, string?>
        {
            ["localFeedback:signals:0:pullRequestId"] = "42",
            // Thread 0: missing threadId (skipped)
            ["localFeedback:signals:0:threads:0:author"] = "a@example.com",
            ["localFeedback:signals:0:threads:0:comments:0:author"] = "a@example.com",
            ["localFeedback:signals:0:threads:0:comments:0:body"] = "c",
            // Thread 1: valid
            ["localFeedback:signals:0:threads:1:threadId"] = "valid-thread",
            ["localFeedback:signals:0:threads:1:comments:0:author"] = "a@example.com",
            ["localFeedback:signals:0:threads:1:comments:0:body"] = "c",
        });

        var optionsMonitor = BuildOptionsMonitor(config);
        var source = new LocalFeedbackSource(optionsMonitor, NullLogger<LocalFeedbackSource>.Instance);

        try
        {
            var query = new FeedbackQuery
            {
                OpenPrs = new List<PrUnderTest>
                {
                    new() { PullRequestId = "42" },
                },
            };

            var signals = await source.PollAsync(query, CancellationToken.None);

            Assert.Single(signals);
            // Only the valid thread should be present
            Assert.Single(signals[0].Threads);
            Assert.Equal("valid-thread", signals[0].Threads[0].ThreadId);
        }
        finally
        {
            source.Dispose();
        }
    }

    [Fact]
    public async Task PollAsync_FileLevelThread_MapsCorrectly()
    {
        var config = BuildConfiguration(new Dictionary<string, string?>
        {
            ["localFeedback:signals:0:pullRequestId"] = "42",
            ["localFeedback:signals:0:threads:0:threadId"] = "pr-level-thread",
            ["localFeedback:signals:0:threads:0:isFileLevel"] = "true",
            ["localFeedback:signals:0:threads:0:comments:0:author"] = "a@example.com",
            ["localFeedback:signals:0:threads:0:comments:0:body"] = "General PR comment",
        });

        var optionsMonitor = BuildOptionsMonitor(config);
        var source = new LocalFeedbackSource(optionsMonitor, NullLogger<LocalFeedbackSource>.Instance);

        try
        {
            var query = new FeedbackQuery
            {
                OpenPrs = new List<PrUnderTest>
                {
                    new() { PullRequestId = "42" },
                },
            };

            var signals = await source.PollAsync(query, CancellationToken.None);

            Assert.Single(signals);
            var thread = signals[0].Threads[0];
            Assert.True(thread.IsFileLevel);
            Assert.Null(thread.FilePath);
            Assert.Null(thread.StartLine);
            Assert.Null(thread.EndLine);
        }
        finally
        {
            source.Dispose();
        }
    }

    [Fact]
    public async Task PollAsync_ReplyComments_MarkedCorrectly()
    {
        var config = BuildConfiguration(new Dictionary<string, string?>
        {
            ["localFeedback:signals:0:pullRequestId"] = "42",
            ["localFeedback:signals:0:threads:0:threadId"] = "t1",
            ["localFeedback:signals:0:threads:0:comments:0:author"] = "reviewer@example.com",
            ["localFeedback:signals:0:threads:0:comments:0:body"] = "Original comment",
            ["localFeedback:signals:0:threads:0:comments:0:isReply"] = "false",
            ["localFeedback:signals:0:threads:0:comments:1:author"] = "author@example.com",
            ["localFeedback:signals:0:threads:0:comments:1:body"] = "Reply to review",
            ["localFeedback:signals:0:threads:0:comments:1:isReply"] = "true",
        });

        var optionsMonitor = BuildOptionsMonitor(config);
        var source = new LocalFeedbackSource(optionsMonitor, NullLogger<LocalFeedbackSource>.Instance);

        try
        {
            var query = new FeedbackQuery
            {
                OpenPrs = new List<PrUnderTest>
                {
                    new() { PullRequestId = "42" },
                },
            };

            var signals = await source.PollAsync(query, CancellationToken.None);

            Assert.Single(signals);
            var comments = signals[0].Threads[0].Comments;
            Assert.Equal(2, comments.Count);
            Assert.False(comments[0].IsReply);
            Assert.True(comments[1].IsReply);
        }
        finally
        {
            source.Dispose();
        }
    }

    [Fact]
    public async Task PollAsync_NoConfig_ReturnsEmpty()
    {
        var config = BuildConfiguration(new Dictionary<string, string?>
        {
            // No localFeedback config at all
        });

        var optionsMonitor = BuildOptionsMonitor(config);
        var source = new LocalFeedbackSource(optionsMonitor, NullLogger<LocalFeedbackSource>.Instance);

        try
        {
            var query = new FeedbackQuery
            {
                OpenPrs = new List<PrUnderTest>
                {
                    new() { PullRequestId = "42" },
                },
            };

            var signals = await source.PollAsync(query, CancellationToken.None);

            Assert.Empty(signals);
        }
        finally
        {
            source.Dispose();
        }
    }

    // ── Idempotency: second poll returns same results ────────────────

    [Fact]
    public async Task PollAsync_SecondCall_ReturnsSameResults()
    {
        var config = BuildConfiguration(new Dictionary<string, string?>
        {
            ["localFeedback:signals:0:pullRequestId"] = "42",
            ["localFeedback:signals:0:threads:0:threadId"] = "t1",
            ["localFeedback:signals:0:threads:0:comments:0:author"] = "a@example.com",
            ["localFeedback:signals:0:threads:0:comments:0:body"] = "c",
        });

        var optionsMonitor = BuildOptionsMonitor(config);
        var source = new LocalFeedbackSource(optionsMonitor, NullLogger<LocalFeedbackSource>.Instance);

        try
        {
            var query = new FeedbackQuery
            {
                OpenPrs = new List<PrUnderTest>
                {
                    new() { PullRequestId = "42" },
                },
            };

            var first = await source.PollAsync(query, CancellationToken.None);
            var second = await source.PollAsync(query, CancellationToken.None);

            Assert.Equal(first.Count, second.Count);
            Assert.Equal(first[0].PullRequestId, second[0].PullRequestId);
            Assert.Equal(first[0].Threads[0].ThreadId, second[0].Threads[0].ThreadId);
        }
        finally
        {
            source.Dispose();
        }
    }

    // ── DI registration tests ────────────────────────────────────────

    [Fact]
    public void LocalFeedbackSource_DiRegistration_Succeeds()
    {
        var config = BuildConfiguration(new Dictionary<string, string?>
        {
            ["localFeedback:signals:0:pullRequestId"] = "42",
            ["localFeedback:signals:0:threads:0:threadId"] = "t1",
            ["localFeedback:signals:0:threads:0:comments:0:author"] = "a@example.com",
            ["localFeedback:signals:0:threads:0:comments:0:body"] = "c",
        });

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddLogging();
        services.AddAgentControllerOptions(config);
        services.AddAgentControllerLocalFeedbackSource();

        var provider = services.BuildServiceProvider();
        var feedbackSource = provider.GetRequiredService<IFeedbackSource>();

        Assert.IsType<LocalFeedbackSource>(feedbackSource);
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static IConfiguration BuildConfiguration(
        Dictionary<string, string?>? overrides = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["agentController:workerId"] = "test-worker",
            ["agentController:pollIntervalSeconds"] = "30",
            ["agentController:maxConcurrentRuns"] = "3",
            ["agentController:runRoot"] = "/tmp/runs",
            ["persistence:provider"] = "Sqlite",
            ["persistence:connectionString"] = "Data Source=test.db",
            ["workSource:provider"] = "LocalFile",
            ["sourceControl:provider"] = "LocalFake",
            ["environmentProvider:provider"] = "LocalWorkspace",
            ["runtime:provider"] = "NoOp",
        };

        if (overrides is not null)
        {
            foreach (var (key, value) in overrides)
            {
                values[key] = value;
            }
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values!).Build();
    }

    private static IOptionsMonitor<LocalFeedbackOptions> BuildOptionsMonitor(
        IConfiguration config)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services
            .AddOptions<LocalFeedbackOptions>()
            .Bind(config.GetSection(LocalFeedbackOptions.SectionName));

        return services.BuildServiceProvider()
            .GetRequiredService<IOptionsMonitor<LocalFeedbackOptions>>();
    }
}

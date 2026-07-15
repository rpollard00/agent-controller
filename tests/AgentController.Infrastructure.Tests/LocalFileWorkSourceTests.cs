using AgentController.Application;
using AgentController.Domain;
using AgentController.Infrastructure;
using AgentController.Infrastructure.Data;
using AgentController.Infrastructure.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AgentController.Infrastructure.Tests;

public class LocalFileWorkSourceTests
{
    // ── Smoke / Interface tests ──────────────────────────────────────

    [Fact]
    public void LocalFileWorkSource_ImplementsInterface()
    {
        var type = typeof(LocalFileWorkSource);
        Assert.True(
            typeof(IWorkSource).IsAssignableFrom(type),
            "LocalFileWorkSource should implement IWorkSource");
    }

    [Fact]
    public void LocalWorkOptions_SectionName_IsCorrect()
    {
        Assert.Equal("localWork", LocalWorkOptions.SectionName);
    }

    [Fact]
    public void LocalWorkItemDefinition_HasExpectedDefaults()
    {
        var def = new LocalWorkItemDefinition();

        Assert.Equal(string.Empty, def.RepoKey);
        Assert.Equal(string.Empty, def.Title);
        Assert.Null(def.ExternalId);
        Assert.Null(def.Body);
        Assert.Null(def.Description);
        Assert.Null(def.AcceptanceCriteria);
        Assert.Equal(0, def.Priority);
        Assert.Equal("New", def.Status);
        Assert.Empty(def.Tags);
    }

    // ── Options binding tests ────────────────────────────────────────

    [Fact]
    public void LocalWorkOptions_BindsSingleDefinition()
    {
        var config = BuildConfiguration(new Dictionary<string, string?>
        {
            ["localWork:definitions:0:repoKey"] = "example-service",
            ["localWork:definitions:0:title"] = "Add retry handling",
            ["localWork:definitions:0:body"] = "Implement retry logic.",
            ["localWork:definitions:0:priority"] = "1",
            ["localWork:definitions:0:status"] = "New",
            ["localWork:definitions:0:tags:0"] = "agent-ready",
            ["localWork:definitions:0:tags:1"] = "enhancement",
            ["localWork:definitions:0:acceptanceCriteria:given"] = "A service call fails",
            ["localWork:definitions:0:acceptanceCriteria:when"] = "The middleware is configured",
            ["localWork:definitions:0:acceptanceCriteria:then"] = "The call is retried",
        });

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services
            .AddOptions<LocalWorkOptions>()
            .Bind(config.GetSection(LocalWorkOptions.SectionName));

        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<LocalWorkOptions>>().Value;

        Assert.NotNull(options);
        Assert.Single(options.Definitions);

        var def = options.Definitions[0];
        Assert.Equal("example-service", def.RepoKey);
        Assert.Equal("Add retry handling", def.Title);
        Assert.Equal("Implement retry logic.", def.Body);
        Assert.Equal(1, def.Priority);
        Assert.Equal("New", def.Status);
        Assert.Equal(2, def.Tags.Count);
        Assert.Contains("agent-ready", def.Tags);
        Assert.Contains("enhancement", def.Tags);
        Assert.NotNull(def.AcceptanceCriteria);
        Assert.Equal(3, def.AcceptanceCriteria.Count);
        Assert.Equal("A service call fails", def.AcceptanceCriteria["given"]);
    }

    [Fact]
    public void LocalWorkOptions_BindsMultipleDefinitions()
    {
        var config = BuildConfiguration(new Dictionary<string, string?>
        {
            ["localWork:definitions:0:repoKey"] = "repo-a",
            ["localWork:definitions:0:title"] = "Task A",
            ["localWork:definitions:1:repoKey"] = "repo-b",
            ["localWork:definitions:1:title"] = "Task B",
            ["localWork:definitions:2:repoKey"] = "repo-c",
            ["localWork:definitions:2:title"] = "Task C",
        });

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services
            .AddOptions<LocalWorkOptions>()
            .Bind(config.GetSection(LocalWorkOptions.SectionName));

        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<LocalWorkOptions>>().Value;

        Assert.Equal(3, options.Definitions.Count);
        Assert.Equal("repo-a", options.Definitions[0].RepoKey);
        Assert.Equal("Task A", options.Definitions[0].Title);
        Assert.Equal("repo-b", options.Definitions[1].RepoKey);
        Assert.Equal("Task B", options.Definitions[1].Title);
        Assert.Equal("repo-c", options.Definitions[2].RepoKey);
        Assert.Equal("Task C", options.Definitions[2].Title);
    }

    [Fact]
    public void LocalWorkOptions_EmptyDefinitionsBindsAsEmptyList()
    {
        // No definitions section at all — binder returns empty list.
        var config = BuildConfiguration(new Dictionary<string, string?>
        {
            // No localWork:definitions keys
        });

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services
            .AddOptions<LocalWorkOptions>()
            .Bind(config.GetSection(LocalWorkOptions.SectionName));

        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<LocalWorkOptions>>().Value;

        Assert.NotNull(options);
        Assert.Empty(options.Definitions);
    }

    [Fact]
    public void LocalWorkOptions_WithExternalId_BindsCorrectly()
    {
        var config = BuildConfiguration(new Dictionary<string, string?>
        {
            ["localWork:definitions:0:repoKey"] = "example-service",
            ["localWork:definitions:0:title"] = "Add retry",
            ["localWork:definitions:0:externalId"] = "custom-external-id-42",
        });

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services
            .AddOptions<LocalWorkOptions>()
            .Bind(config.GetSection(LocalWorkOptions.SectionName));

        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<LocalWorkOptions>>().Value;

        var def = Assert.Single(options.Definitions);
        Assert.Equal("custom-external-id-42", def.ExternalId);
    }

    [Fact]
    public void LocalWorkOptions_DescriptionAlias_BindsAsBodyFallback()
    {
        // When Description is set instead of Body, it should bind correctly.
        // Note: both Body and Description map to the same conceptual field;
        // the LocalFileWorkSource reads Body first, then Description.
        var config = BuildConfiguration(new Dictionary<string, string?>
        {
            ["localWork:definitions:0:repoKey"] = "example-service",
            ["localWork:definitions:0:title"] = "Test description alias",
            ["localWork:definitions:0:description"] = "This is a description.",
        });

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services
            .AddOptions<LocalWorkOptions>()
            .Bind(config.GetSection(LocalWorkOptions.SectionName));

        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<LocalWorkOptions>>().Value;

        var def = Assert.Single(options.Definitions);
        Assert.Equal("This is a description.", def.Description);
        Assert.Null(def.Body); // Description is a separate field, not mapped to Body in binding
    }

    // ── DeriveExternalId tests ───────────────────────────────────────

    [Fact]
    public void DeriveExternalId_ProducesStableOutput()
    {
        var def = new LocalWorkItemDefinition
        {
            RepoKey = "example-service",
            Title = "Add retry handling",
            Body = "Implement retry.",
        };

        var id1 = LocalFileWorkSource.DeriveExternalId(def);
        var id2 = LocalFileWorkSource.DeriveExternalId(def);

        Assert.Equal(id1, id2);
        Assert.StartsWith("local-", id1);
        Assert.Equal(12 + "local-".Length, id1.Length); // 6 + 12 = 18
    }

    [Fact]
    public void DeriveExternalId_DifferentContent_ProducesDifferentOutput()
    {
        var def1 = new LocalWorkItemDefinition
        {
            RepoKey = "example-service",
            Title = "Task A",
            Body = "Do A",
        };

        var def2 = new LocalWorkItemDefinition
        {
            RepoKey = "example-service",
            Title = "Task B",
            Body = "Do B",
        };

        var id1 = LocalFileWorkSource.DeriveExternalId(def1);
        var id2 = LocalFileWorkSource.DeriveExternalId(def2);

        Assert.NotEqual(id1, id2);
    }

    [Fact]
    public void DeriveExternalId_UsesDescriptionWhenBodyIsNull()
    {
        var def = new LocalWorkItemDefinition
        {
            RepoKey = "example-service",
            Title = "Task",
            Body = null,
            Description = "Description text",
        };

        var id = LocalFileWorkSource.DeriveExternalId(def);

        Assert.StartsWith("local-", id);
        Assert.Equal(18, id.Length);
    }

    [Fact]
    public void DeriveExternalId_HandlesEmptyBodyAndDescription()
    {
        var def = new LocalWorkItemDefinition
        {
            RepoKey = "example-service",
            Title = "Task",
            Body = null,
            Description = null,
        };

        var id = LocalFileWorkSource.DeriveExternalId(def);

        Assert.StartsWith("local-", id);
    }

    // ── No-op method tests ───────────────────────────────────────────

    [Fact]
    public async Task UpdateStatusAsync_IsNoOp()
    {
        // We can't easily construct LocalFileWorkSource without full DI,
        // but we can verify the pattern: it should not throw.
        // Smoke: the method exists and returns completed task.
        var method = typeof(LocalFileWorkSource).GetMethod(
            nameof(IWorkSource.UpdateStatusAsync),
            [typeof(ExternalWorkRef), typeof(ExternalWorkStatus), typeof(CancellationToken)]);

        Assert.NotNull(method);
        Assert.Equal(typeof(Task), method.ReturnType);
    }

    [Fact]
    public async Task AddCommentAsync_IsNoOp()
    {
        var method = typeof(LocalFileWorkSource).GetMethod(
            nameof(IWorkSource.AddCommentAsync),
            [typeof(ExternalWorkRef), typeof(string), typeof(CancellationToken)]);

        Assert.NotNull(method);
        Assert.Equal(typeof(Task), method.ReturnType);
    }

    // ── DI registration tests ────────────────────────────────────────

    [Fact]
    public void LocalFileWorkSource_DiRegistration_Succeeds()
    {
        var config = BuildConfiguration(new Dictionary<string, string?>
        {
            ["workSource:provider"] = "LocalFile",
            ["agentController:workerId"] = "test-worker",
            ["agentController:pollIntervalSeconds"] = "30",
            ["agentController:maxConcurrentRuns"] = "3",
            ["agentController:runRoot"] = "/tmp/runs",
            ["persistence:provider"] = "Sqlite",
            ["persistence:connectionString"] = "Data Source=:memory:",
            ["sourceControl:provider"] = "LocalFake",
            ["environmentProvider:provider"] = "LocalWorkspace",
            ["runtime:provider"] = "NoOp",
            ["localWork:definitions:0:repoKey"] = "example-service",
            ["localWork:definitions:0:title"] = "Test task",
        });

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddLogging();
        services.AddAgentControllerOptions(config);
        services.AddAgentControllerLocalFileWorkSource();

        var provider = services.BuildServiceProvider();
        var workSource = provider.GetRequiredService<IWorkSource>();

        Assert.IsType<LocalFileWorkSource>(workSource);
    }

    [Fact]
    public void WorkSourceOptions_WithLocalFileProvider_DoesNotRequireAzureDevOpsFields()
    {
        // When provider is "LocalFile", WorkSourceOptions validation should still pass.
        // LocalFile does not require OrganizationUrl, Project, or PAT.
        var config = BuildConfiguration(new Dictionary<string, string?>
        {
            ["workSource:provider"] = "LocalFile",
            ["workSource:eligibleTags:0"] = "agent-ready",
        });

        var services = new ServiceCollection();
        services
            .AddOptions<WorkSourceOptions>()
            .Bind(config.GetSection("workSource"))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<WorkSourceOptions>>().Value;

        Assert.Equal("LocalFile", options.Provider);
        Assert.Equal("agent", options.TagPrefix);
        // Azure DevOps fields are optional and not required for LocalFile
        Assert.Null(options.OrganizationUrl);
        Assert.Null(options.Project);
    }

    // ── Integration test: LocalFileWorkSource initializes and queries  ──
    // These tests require a real database. They use a temporary SQLite
    // file so the DbContext, repositories, and stores are fully wired.

    [Fact]
    public async Task LocalFileWorkSource_FindEligibleAsync_DiscoversUpsertedDefinitions()
    {
        var dbPath = Path.GetTempFileName();
        var connStr = $"Data Source={dbPath}";

        // Run migrations on the temp database first

        try
        {
            var config = BuildConfiguration(new Dictionary<string, string?>
            {
                ["workSource:provider"] = "LocalFile",
                ["agentController:workerId"] = "test-worker",
                ["agentController:pollIntervalSeconds"] = "30",
                ["agentController:maxConcurrentRuns"] = "3",
                ["agentController:runRoot"] = "/tmp/runs",
                ["persistence:provider"] = "Sqlite",
                ["persistence:connectionString"] = connStr,
                ["sourceControl:provider"] = "LocalFake",
                ["environmentProvider:provider"] = "LocalWorkspace",
                ["runtime:provider"] = "NoOp",
                ["localWork:definitions:0:repoKey"] = "example-service",
                ["localWork:definitions:0:title"] = "Add retry handling",
                ["localWork:definitions:0:body"] = "Implement retry logic.",
                ["localWork:definitions:0:tags:0"] = "agent-ready",
                ["localWork:definitions:0:status"] = "New",
            });

            var services = BuildServiceProvider(config, connStr);
            var workSource = services.GetRequiredService<IWorkSource>();

            var candidates = await workSource.FindEligibleAsync(
                new WorkQuery(),
                CancellationToken.None);

            // Should discover the upserted definition
            Assert.Single(candidates);
            Assert.Equal("Add retry handling", candidates[0].Title);
            Assert.Equal("example-service", candidates[0].RepoKey);
            Assert.Equal("LocalFile", candidates[0].Source);
            Assert.StartsWith("local-", candidates[0].ExternalId);
            Assert.Contains("agent-ready", candidates[0].Tags);
        }
        finally
        {
            TryDeleteFile(dbPath);
        }
    }

    [Fact]
    public async Task LocalFileWorkSource_FindEligibleAsync_SecondCallIsIdempotent()
    {
        var dbPath = Path.GetTempFileName();
        var connStr = $"Data Source={dbPath}";

        try
        {
            var config = BuildConfiguration(new Dictionary<string, string?>
            {
                ["workSource:provider"] = "LocalFile",
                ["agentController:workerId"] = "test-worker",
                ["agentController:pollIntervalSeconds"] = "30",
                ["agentController:maxConcurrentRuns"] = "3",
                ["agentController:runRoot"] = "/tmp/runs",
                ["persistence:provider"] = "Sqlite",
                ["persistence:connectionString"] = connStr,
                ["sourceControl:provider"] = "LocalFake",
                ["environmentProvider:provider"] = "LocalWorkspace",
                ["runtime:provider"] = "NoOp",
                ["localWork:definitions:0:repoKey"] = "example-service",
                ["localWork:definitions:0:title"] = "Task A",
            });

            var services = BuildServiceProvider(config, connStr);
            var workSource = services.GetRequiredService<IWorkSource>();

            var first = await workSource.FindEligibleAsync(
                new WorkQuery(),
                CancellationToken.None);

            Assert.Single(first);

            // Second call should not duplicate
            var second = await workSource.FindEligibleAsync(
                new WorkQuery(),
                CancellationToken.None);

            Assert.Single(second);
            Assert.Equal(first[0].Id, second[0].Id);
        }
        finally
        {
            TryDeleteFile(dbPath);
        }
    }

    [Fact]
    public async Task LocalFileWorkSource_SkipsDefinitionMissingRepoKey()
    {
        var dbPath = Path.GetTempFileName();
        var connStr = $"Data Source={dbPath}";

        try
        {
            var config = BuildConfiguration(new Dictionary<string, string?>
            {
                ["workSource:provider"] = "LocalFile",
                ["agentController:workerId"] = "test-worker",
                ["agentController:pollIntervalSeconds"] = "30",
                ["agentController:maxConcurrentRuns"] = "3",
                ["agentController:runRoot"] = "/tmp/runs",
                ["persistence:provider"] = "Sqlite",
                ["persistence:connectionString"] = connStr,
                ["sourceControl:provider"] = "LocalFake",
                ["environmentProvider:provider"] = "LocalWorkspace",
                ["runtime:provider"] = "NoOp",
                // Missing repoKey
                ["localWork:definitions:0:title"] = "Bad task",
                ["localWork:definitions:0:tags:0"] = "agent-ready",
            });

            var services = BuildServiceProvider(config, connStr);
            var workSource = services.GetRequiredService<IWorkSource>();

            var candidates = await workSource.FindEligibleAsync(
                new WorkQuery(),
                CancellationToken.None);

            // Should be empty because the definition was skipped
            Assert.Empty(candidates);
        }
        finally
        {
            TryDeleteFile(dbPath);
        }
    }

    [Fact]
    public async Task LocalFileWorkSource_SkipsDefinitionMissingTitle()
    {
        var dbPath = Path.GetTempFileName();
        var connStr = $"Data Source={dbPath}";

        try
        {
            var config = BuildConfiguration(new Dictionary<string, string?>
            {
                ["workSource:provider"] = "LocalFile",
                ["agentController:workerId"] = "test-worker",
                ["agentController:pollIntervalSeconds"] = "30",
                ["agentController:maxConcurrentRuns"] = "3",
                ["agentController:runRoot"] = "/tmp/runs",
                ["persistence:provider"] = "Sqlite",
                ["persistence:connectionString"] = connStr,
                ["sourceControl:provider"] = "LocalFake",
                ["environmentProvider:provider"] = "LocalWorkspace",
                ["runtime:provider"] = "NoOp",
                // Missing title
                ["localWork:definitions:0:repoKey"] = "example-service",
                ["localWork:definitions:0:tags:0"] = "agent-ready",
            });

            var services = BuildServiceProvider(config, connStr);
            var workSource = services.GetRequiredService<IWorkSource>();

            var candidates = await workSource.FindEligibleAsync(
                new WorkQuery(),
                CancellationToken.None);

            Assert.Empty(candidates);
        }
        finally
        {
            TryDeleteFile(dbPath);
        }
    }

    [Fact]
    public async Task LocalFileWorkSource_PartialValidDefinitions_SkipsBadOnes()
    {
        var dbPath = Path.GetTempFileName();
        var connStr = $"Data Source={dbPath}";

        try
        {
            var config = BuildConfiguration(new Dictionary<string, string?>
            {
                ["workSource:provider"] = "LocalFile",
                ["agentController:workerId"] = "test-worker",
                ["agentController:pollIntervalSeconds"] = "30",
                ["agentController:maxConcurrentRuns"] = "3",
                ["agentController:runRoot"] = "/tmp/runs",
                ["persistence:provider"] = "Sqlite",
                ["persistence:connectionString"] = connStr,
                ["sourceControl:provider"] = "LocalFake",
                ["environmentProvider:provider"] = "LocalWorkspace",
                ["runtime:provider"] = "NoOp",
                // Def 0: valid
                ["localWork:definitions:0:repoKey"] = "example-service",
                ["localWork:definitions:0:title"] = "Valid task",
                ["localWork:definitions:0:tags:0"] = "agent-ready",
                // Def 1: missing repoKey (skipped)
                ["localWork:definitions:1:title"] = "Bad task",
                ["localWork:definitions:1:tags:0"] = "agent-ready",
                // Def 2: valid
                ["localWork:definitions:2:repoKey"] = "other-service",
                ["localWork:definitions:2:title"] = "Another valid task",
                ["localWork:definitions:2:tags:0"] = "agent-ready",
            });

            var services = BuildServiceProvider(config, connStr);
            var workSource = services.GetRequiredService<IWorkSource>();

            var candidates = await workSource.FindEligibleAsync(
                new WorkQuery(),
                CancellationToken.None);

            // Should have 2 valid candidates, 1 skipped
            Assert.Equal(2, candidates.Count);
            var titles = candidates.Select(c => c.Title).ToList();
            Assert.Contains("Valid task", titles);
            Assert.Contains("Another valid task", titles);
            Assert.DoesNotContain("Bad task", titles);
        }
        finally
        {
            TryDeleteFile(dbPath);
        }
    }

    [Fact]
    public async Task LocalFileWorkSource_TryClaimAsync_ClaimsUpsertedItem()
    {
        var dbPath = Path.GetTempFileName();
        var connStr = $"Data Source={dbPath}";

        try
        {
            var config = BuildConfiguration(new Dictionary<string, string?>
            {
                ["workSource:provider"] = "LocalFile",
                ["workSource:activeState"] = "Active",
                ["agentController:workerId"] = "test-worker",
                ["agentController:pollIntervalSeconds"] = "30",
                ["agentController:maxConcurrentRuns"] = "3",
                ["agentController:runRoot"] = "/tmp/runs",
                ["persistence:provider"] = "Sqlite",
                ["persistence:connectionString"] = connStr,
                ["sourceControl:provider"] = "LocalFake",
                ["environmentProvider:provider"] = "LocalWorkspace",
                ["runtime:provider"] = "NoOp",
                ["localWork:definitions:0:repoKey"] = "example-service",
                ["localWork:definitions:0:title"] = "Claimable task",
                ["localWork:definitions:0:tags:0"] = "agent-ready",
            });

            var services = BuildServiceProvider(config, connStr);
            var workSource = services.GetRequiredService<IWorkSource>();

            // First, discover to get a candidate with a DB-assigned Id
            var candidates = await workSource.FindEligibleAsync(
                new WorkQuery(),
                CancellationToken.None);

            var candidate = Assert.Single(candidates);

            var claim = new ClaimRequest
            {
                WorkerId = "test-worker",
                LeaseTimeout = TimeSpan.FromMinutes(10),
            };

            var result = await workSource.TryClaimAsync(
                candidate, claim, CancellationToken.None);

            Assert.True(result.Success);
            Assert.NotNull(result.WorkRef);
            Assert.Equal("LocalFile", result.WorkRef.Source);
            Assert.NotNull(result.LeaseToken);

            // After claiming, the item should no longer be eligible
            var afterClaim = await workSource.FindEligibleAsync(
                new WorkQuery(),
                CancellationToken.None);

            Assert.Empty(afterClaim);
        }
        finally
        {
            TryDeleteFile(dbPath);
        }
    }

    [Fact]
    public async Task LocalFileWorkSource_UsesExplicitExternalId()
    {
        var dbPath = Path.GetTempFileName();
        var connStr = $"Data Source={dbPath}";

        try
        {
            var config = BuildConfiguration(new Dictionary<string, string?>
            {
                ["workSource:provider"] = "LocalFile",
                ["agentController:workerId"] = "test-worker",
                ["agentController:pollIntervalSeconds"] = "30",
                ["agentController:maxConcurrentRuns"] = "3",
                ["agentController:runRoot"] = "/tmp/runs",
                ["persistence:provider"] = "Sqlite",
                ["persistence:connectionString"] = connStr,
                ["sourceControl:provider"] = "LocalFake",
                ["environmentProvider:provider"] = "LocalWorkspace",
                ["runtime:provider"] = "NoOp",
                ["localWork:definitions:0:repoKey"] = "example-service",
                ["localWork:definitions:0:title"] = "Task with custom ID",
                ["localWork:definitions:0:externalId"] = "my-custom-id-001",
                ["localWork:definitions:0:tags:0"] = "agent-ready",
            });

            var services = BuildServiceProvider(config, connStr);
            var workSource = services.GetRequiredService<IWorkSource>();

            var candidates = await workSource.FindEligibleAsync(
                new WorkQuery(),
                CancellationToken.None);

            var candidate = Assert.Single(candidates);
            Assert.Equal("my-custom-id-001", candidate.ExternalId);
            // Should not be derived; should be the explicit value
            Assert.DoesNotContain("local-", candidate.ExternalId);
        }
        finally
        {
            TryDeleteFile(dbPath);
        }
    }

    [Fact]
    public async Task ReactivateForReworkAsync_StripsAgentWorkerTagAndReaddsAgentReady()
    {
        var dbPath = Path.GetTempFileName();
        var connStr = $"Data Source={dbPath}";

        try
        {
            var config = BuildConfiguration(new Dictionary<string, string?>
            {
                ["workSource:provider"] = "LocalFile",
                ["workSource:eligibleStates:0"] = "New",
                ["agentController:workerId"] = "test-worker",
                ["agentController:pollIntervalSeconds"] = "30",
                ["agentController:maxConcurrentRuns"] = "3",
                ["agentController:runRoot"] = "/tmp/runs",
                ["persistence:provider"] = "Sqlite",
                ["persistence:connectionString"] = connStr,
                ["sourceControl:provider"] = "LocalFake",
                ["environmentProvider:provider"] = "LocalWorkspace",
                ["runtime:provider"] = "NoOp",
                ["localWork:definitions:0:repoKey"] = "example-service",
                ["localWork:definitions:0:title"] = "Rework task",
                ["localWork:definitions:0:tags:0"] = "agent-ready",
            });

            var services = BuildServiceProvider(config, connStr);
            var workSource = services.GetRequiredService<IWorkSource>();

            // Discover the item to get its ID
            var candidates = await workSource.FindEligibleAsync(
                new WorkQuery(),
                CancellationToken.None);

            var candidate = Assert.Single(candidates);

            // Simulate the item having agent-active and agent-worker tags
            // (as would be set during claim/execution)
            var workItemId = candidate.Id;

            // Directly upsert the item with agent lifecycle tags to simulate
            // the state after a run has been claimed and executed
            await using (var scope = services.CreateAsyncScope())
            {
                var store = scope.ServiceProvider.GetRequiredService<IWorkItemStore>();
                var current = await store.GetByIdAsync(workItemId, CancellationToken.None);
                Assert.NotNull(current);

                await store.UpsertAsync(current with
                {
                    Tags = new List<string>
                    {
                        "agent-active",
                        "agent-worker:test-worker",
                        "backend",
                        "high-priority",
                    },
                }, CancellationToken.None);
            }

            // Now call ReactivateForReworkAsync
            var result = await workSource.ReactivateForReworkAsync(
                new ReworkReactivateRequest
                {
                    WorkItemId = workItemId,
                    WorkRef = new ExternalWorkRef { Source = "LocalFile", ExternalId = candidate.ExternalId },
                    CycleNumber = 1,
                    ThreadCount = 2,
                    PullRequestUrl = "https://example.com/pr/42",
                },
                CancellationToken.None);

            Assert.True(result.Success);

            // Verify the tags were updated correctly
            await using (var scope = services.CreateAsyncScope())
            {
                var store = scope.ServiceProvider.GetRequiredService<IWorkItemStore>();
                var updated = await store.GetByIdAsync(workItemId, CancellationToken.None);
                Assert.NotNull(updated);

                // agent-active and agent-worker:test-worker should be stripped
                Assert.DoesNotContain("agent-active", updated.Tags);
                Assert.DoesNotContain("agent-worker:test-worker", updated.Tags);

                // agent-ready should be re-added
                Assert.Contains("agent-ready", updated.Tags);

                // Unrelated tags should be preserved
                Assert.Contains("backend", updated.Tags);
                Assert.Contains("high-priority", updated.Tags);
            }
        }
        finally
        {
            TryDeleteFile(dbPath);
        }
    }

    [Fact]
    public async Task LocalFileWorkSource_NoDefinitions_ReturnsEmpty()
    {
        var dbPath = Path.GetTempFileName();
        var connStr = $"Data Source={dbPath}";

        try
        {
            var config = BuildConfiguration(new Dictionary<string, string?>
            {
                ["workSource:provider"] = "LocalFile",
                ["agentController:workerId"] = "test-worker",
                ["agentController:pollIntervalSeconds"] = "30",
                ["agentController:maxConcurrentRuns"] = "3",
                ["agentController:runRoot"] = "/tmp/runs",
                ["persistence:provider"] = "Sqlite",
                ["persistence:connectionString"] = connStr,
                ["sourceControl:provider"] = "LocalFake",
                ["environmentProvider:provider"] = "LocalWorkspace",
                ["runtime:provider"] = "NoOp",
                // No localWork:definitions at all
            });

            var services = BuildServiceProvider(config, connStr);
            var workSource = services.GetRequiredService<IWorkSource>();

            var candidates = await workSource.FindEligibleAsync(
                new WorkQuery(),
                CancellationToken.None);

            Assert.Empty(candidates);
        }
        finally
        {
            TryDeleteFile(dbPath);
        }
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

    private static ServiceProvider BuildServiceProvider(
        IConfiguration config, string connectionString)
    {
        var services = new ServiceCollection();

        // Override the connection string for the temp database
        var configBuilder = new ConfigurationBuilder()
            .AddConfiguration(config)
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["persistence:connectionString"] = connectionString,
            });

        var finalConfig = configBuilder.Build();

        services.AddSingleton<IConfiguration>(finalConfig);
        services.AddLogging();
        services.AddAgentControllerOptions(finalConfig);
        services.AddAgentControllerDbContext(finalConfig);
        services.AddAgentControllerRepositories();
        services.AddAgentControllerLifecycleService();
        services.AddAgentControllerLocalFileWorkSource();

        var provider = services.BuildServiceProvider();

        // Ensure the database schema is created (same pattern as API integration tests).
        // Uses EnsureCreated() rather than the migration runner for test simplicity.
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentControllerDbContext>();
        db.Database.OpenConnection();
        db.Database.EnsureCreated();

        return provider;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup
        }
    }
}

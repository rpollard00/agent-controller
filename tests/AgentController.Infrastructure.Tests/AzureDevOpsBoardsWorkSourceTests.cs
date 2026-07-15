using System.Collections.Generic;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AgentController.Application;
using AgentController.Domain;
using AgentController.Infrastructure;
using AgentController.Infrastructure.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AgentController.Infrastructure.Tests;

/// <summary>
/// Tests for <see cref="AzureDevOpsBoardsWorkSource.ReactivateForReworkAsync"/>
/// verifying the single-PATCH reactivation flow that strips agent lifecycle tags
/// and re-adds agent-ready atomically.
/// </summary>
public class AzureDevOpsBoardsWorkSourceTests
{
    private const string OrgUrl = "https://dev.azure.com/testorg";
    private const string Project = "TestProject";
    private const string EligibleState = "New";
    private const string ActiveState = "Active";
    private static readonly string[] EmptyStringArray = [];

    [Fact]
    public async Task FindEligibleAsync_PollsAllEnabledManagedEnvironmentsWithProfileSettings()
    {
        var alphaProfile = ManagedEnvironment("alpha", "AlphaProject", ["Ready"], ["agent-ready"]);
        var zetaProfile = ManagedEnvironment("zeta", "ZetaProject", ["Approved"], ["autonomous"]);
        var alphaClient = new MockAzureDevOpsBoardsClient
        {
            QueryResults =
            [
                new WorkCandidate
                {
                    Id = "wi-alpha",
                    ExternalId = "1",
                    Source = "AzureDevOpsBoards",
                },
            ],
        };
        var zetaClient = new MockAzureDevOpsBoardsClient
        {
            QueryResults =
            [
                new WorkCandidate
                {
                    Id = "wi-zeta",
                    ExternalId = "2",
                    Source = "AzureDevOpsBoards",
                },
            ],
        };
        var services = new ServiceCollection();
        services.AddSingleton<IAzureDevOpsBoardsClient>(new MockAzureDevOpsBoardsClient());
        services.AddSingleton<IManagedProfileResolver>(
            new StubManagedProfileResolver([alphaProfile, zetaProfile])
        );
        services.AddSingleton<IAzureDevOpsBoardsClientFactory>(
            new StubClientFactory(
                new Dictionary<string, IAzureDevOpsBoardsClient>
                {
                    ["alpha"] = alphaClient,
                    ["zeta"] = zetaClient,
                }
            )
        );
        var provider = services.BuildServiceProvider();
        var workSource = new AzureDevOpsBoardsWorkSource(
            new DelegatingScopeFactory(provider),
            new FakeOptionsMonitor<WorkSourceOptions>(
                new WorkSourceOptions { Project = "ConfiguredProject" }
            )
        );

        var candidates = await workSource.FindEligibleAsync(
            new WorkQuery { MaxResults = 5 },
            CancellationToken.None
        );

        Assert.Equal(2, candidates.Count);
        Assert.Equal("AlphaProject", Assert.Single(alphaClient.QueryCalls).Project);
        Assert.Equal(["Ready"], alphaClient.QueryCalls[0].ExcludedStates);
        Assert.Null(alphaClient.QueryCalls[0].States);
        Assert.Equal(["agent-ready"], alphaClient.QueryCalls[0].Tags);
        Assert.Equal("ZetaProject", Assert.Single(zetaClient.QueryCalls).Project);
        Assert.Equal(["Approved"], zetaClient.QueryCalls[0].ExcludedStates);
        Assert.Null(zetaClient.QueryCalls[0].States);
        Assert.Equal(["agent-ready"], zetaClient.QueryCalls[0].Tags);
        Assert.Equal("alpha", candidates[0].SourceMetadata?["workSourceEnvironmentKey"]);
        Assert.Equal("zeta", candidates[1].SourceMetadata?["workSourceEnvironmentKey"]);
    }

    // ──────────────────────────────────────────────
    // ReactivateForReworkAsync — single-PATCH success
    // ──────────────────────────────────────────────

    [Fact]
    public async Task ReactivateForReworkAsync_Success_SinglePatchStripsAgentTagsAndAddsAgentReady()
    {
        // Arrange: mock client returns success for the merged PATCH
        var mockClient = new MockAzureDevOpsBoardsClient
        {
            UpdateWorkItemStatusAsyncReturns = true,
        };

        var workSource = CreateWorkSource(mockClient);

        var workRef = new ExternalWorkRef
        {
            Source = "AzureDevOpsBoards",
            ExternalId = "42",
            Revision = "5",
        };

        var request = new ReworkReactivateRequest
        {
            WorkItemId = "wi_42",
            WorkRef = workRef,
            CycleNumber = 2,
            ThreadCount = 3,
            PullRequestUrl = "https://dev.azure.com/testorg/TestProject/_git/repo/pullrequest/10",
        };

        // Act
        var result = await workSource.ReactivateForReworkAsync(request, CancellationToken.None);

        // Assert: reactivation succeeded
        Assert.True(result.Success);
        Assert.Null(result.FailureReason);

        // Assert: exactly one UpdateWorkItemStatusAsync call (single PATCH)
        Assert.Single(mockClient.UpdateWorkItemStatusCalls);
        var call = mockClient.UpdateWorkItemStatusCalls[0];

        // Assert: status is set to the configured active state
        Assert.Equal(ActiveState, call.Status.Status);

        // Assert: RemovedTags contains active, failed, needs-human lifecycle tags and agent-worker:*
        Assert.NotNull(call.Status.RemovedTags);
        Assert.Contains(WorkSourceOptions.TagActive(), call.Status.RemovedTags);
        Assert.Contains(WorkSourceOptions.TagFailed(), call.Status.RemovedTags);
        Assert.Contains(
            WorkSourceOptions.TagNeedsHuman(),
            call.Status.RemovedTags
        );
        Assert.Contains("agent-worker:*", call.Status.RemovedTags);

        // Assert: Tags to add contains agent-ready
        Assert.NotNull(call.Status.Tags);
        Assert.Single(call.Status.Tags);
        Assert.Equal(WorkSourceOptions.TagReady(), call.Status.Tags[0]);

        // Assert: a rework-start comment was posted
        Assert.Single(mockClient.AddCommentCalls);
        Assert.Contains("Rework cycle 2 started", mockClient.AddCommentCalls[0]);
        Assert.Contains("3 review threads", mockClient.AddCommentCalls[0]);
    }

    [Fact]
    public async Task ReactivateForReworkAsync_PatchFailure_ReturnsFailureWithReworkTagStripFailed()
    {
        // Arrange: mock client returns false for the merged PATCH (simulates 412)
        var mockClient = new MockAzureDevOpsBoardsClient
        {
            UpdateWorkItemStatusAsyncReturns = false,
        };

        var workSource = CreateWorkSource(mockClient);

        var workRef = new ExternalWorkRef
        {
            Source = "AzureDevOpsBoards",
            ExternalId = "42",
            Revision = "5",
        };

        var request = new ReworkReactivateRequest
        {
            WorkItemId = "wi_42",
            WorkRef = workRef,
            CycleNumber = 1,
            ThreadCount = 1,
            PullRequestUrl = "https://dev.azure.com/testorg/TestProject/_git/repo/pullrequest/5",
        };

        // Act
        var result = await workSource.ReactivateForReworkAsync(request, CancellationToken.None);

        // Assert: reactivation failed with the expected diagnostic code
        Assert.False(result.Success);
        Assert.NotNull(result.FailureReason);
        Assert.Contains("[rework_tag_strip_failed]", result.FailureReason);
        Assert.Contains(ActiveState, result.FailureReason);

        // Assert: no comment was posted (comment is only posted on success)
        Assert.Empty(mockClient.AddCommentCalls);
    }

    [Fact]
    public async Task ReactivateForReworkAsync_UsesActiveStateAsTarget()
    {
        // Arrange: mock client with custom active state
        var mockClient = new MockAzureDevOpsBoardsClient
        {
            UpdateWorkItemStatusAsyncReturns = true,
        };

        var workSource = CreateWorkSource(mockClient, activeState: "Ready");

        var request = new ReworkReactivateRequest
        {
            WorkItemId = "wi_10",
            WorkRef = new ExternalWorkRef { Source = "AzureDevOpsBoards", ExternalId = "10" },
            CycleNumber = 1,
            ThreadCount = 1,
            PullRequestUrl = "https://example.com/pr/1",
        };

        // Act
        var result = await workSource.ReactivateForReworkAsync(request, CancellationToken.None);

        // Assert: uses the configured active state
        Assert.True(result.Success);
        Assert.Single(mockClient.UpdateWorkItemStatusCalls);
        Assert.Equal("Ready", mockClient.UpdateWorkItemStatusCalls[0].Status.Status);
    }

    [Fact]
    public async Task ReactivateForReworkAsync_NoActiveState_ReturnsFailure()
    {
        // Arrange: no active state configured (ActiveState is the target for reactivation)
        var mockClient = new MockAzureDevOpsBoardsClient();
        var options = new WorkSourceOptions
        {
            Project = Project,
            OrganizationUrl = OrgUrl,
            ActiveState = null, // No active state configured
        };
        var optionsMonitor = new FakeOptionsMonitor<WorkSourceOptions>(options);
        var scopeFactory = CreateScopeFactory(mockClient);
        var workSource = new AzureDevOpsBoardsWorkSource(scopeFactory, optionsMonitor);

        var request = new ReworkReactivateRequest
        {
            WorkItemId = "wi_1",
            WorkRef = new ExternalWorkRef { Source = "AzureDevOpsBoards", ExternalId = "1" },
            CycleNumber = 1,
            ThreadCount = 1,
            PullRequestUrl = "https://example.com/pr/1",
        };

        // Act
        var result = await workSource.ReactivateForReworkAsync(request, CancellationToken.None);

        // Assert: fails with clear reason about missing active state
        Assert.False(result.Success);
        Assert.NotNull(result.FailureReason);
        Assert.Contains(
            "active state",
            result.FailureReason,
            StringComparison.OrdinalIgnoreCase
        );
    }

    [Fact]
    public async Task ReactivateForReworkAsync_NoProjectConfigured_ReturnsFailure()
    {
        // Arrange: no project configured
        var mockClient = new MockAzureDevOpsBoardsClient();
        var workSource = CreateWorkSource(mockClient, project: null);

        var request = new ReworkReactivateRequest
        {
            WorkItemId = "wi_1",
            WorkRef = new ExternalWorkRef { Source = "AzureDevOpsBoards", ExternalId = "1" },
            CycleNumber = 1,
            ThreadCount = 1,
            PullRequestUrl = "https://example.com/pr/1",
        };

        // Act
        var result = await workSource.ReactivateForReworkAsync(request, CancellationToken.None);

        // Assert: fails with project-not-configured reason
        Assert.False(result.Success);
        Assert.NotNull(result.FailureReason);
        Assert.Contains("project", result.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    // ──────────────────────────────────────────────
    // UpdateWorkItemStatusAsync — RemovedTags + 412 hard-fail (HTTP level)
    // ──────────────────────────────────────────────

    [Fact]
    public async Task UpdateWorkItemStatus_RemovedTagsWithWildcard_FetchesCurrentTags_StripsAndPatches()
    {
        // Arrange: fake HTTP handler that returns current tags on GET and 200 on PATCH
        int requestCount = 0;
        string? patchBody = null;
        string? ifMatchValue = null;

        var handler = new CaptureHttpMessageHandler(
            async (req) =>
            {
                requestCount++;

                if (req.Method == HttpMethod.Get)
                {
                    // Return current work item with agent lifecycle tags
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            WorkItemGetResponse(
                                42,
                                7,
                                "Resolved",
                                "agent-active; agent-worker:live-ado_worker; agent-ready; backend; high-priority"
                            ),
                            Encoding.UTF8,
                            "application/json"
                        ),
                    };
                }

                if (req.Method == HttpMethod.Patch)
                {
                    if (req.Content is not null)
                        patchBody = await req.Content.ReadAsStringAsync();

                    ifMatchValue = req.Headers.TryGetValues("If-Match", out var values)
                        ? values.FirstOrDefault()
                        : null;

                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            WorkItemPatchResponse(42, 8),
                            Encoding.UTF8,
                            "application/json"
                        ),
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        );

        var client = CreateClientWithHandler(handler);

        var workRef = new ExternalWorkRef
        {
            Source = "AzureDevOpsBoards",
            ExternalId = "42",
            Revision = "7",
        };

        var status = new ExternalWorkStatus
        {
            Status = "New",
            Tags = new[] { "agent-ready" },
            RemovedTags = new[]
            {
                "agent-active",
                "agent-failed",
                "agent-needs-human",
                "agent-worker:*",
            },
        };

        // Act
        var ok = await client.UpdateWorkItemStatusAsync(workRef, status, CancellationToken.None);

        // Assert: PATCH succeeded
        Assert.True(ok);

        // Assert: two requests — GET (read current) + PATCH (write back)
        Assert.Equal(2, requestCount);

        // Assert: If-Match header sent with revision
        Assert.Equal("7", ifMatchValue);

        // Assert: PATCH body contains state change and merged tags
        Assert.NotNull(patchBody);
        Assert.Contains("System.State", patchBody);
        Assert.Contains("New", patchBody);
        Assert.Contains("System.Tags", patchBody);
        Assert.Contains("agent-ready", patchBody);

        // Assert: agent lifecycle tags were stripped
        Assert.DoesNotContain("agent-active", patchBody!);
        Assert.DoesNotContain("agent-worker:", patchBody!);
        Assert.DoesNotContain("agent-failed", patchBody!);
        Assert.DoesNotContain("agent-needs-human", patchBody!);

        // Assert: unrelated tags preserved
        Assert.Contains("backend", patchBody!);
        Assert.Contains("high-priority", patchBody!);
    }

    [Fact]
    public async Task UpdateWorkItemStatus_RemovedTags_412ReturnsFalse()
    {
        // Arrange: fake handler returns 412 on the PATCH
        var handler = new CaptureHttpMessageHandler(
            async (req) =>
            {
                if (req.Method == HttpMethod.Get)
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            WorkItemGetResponse(
                                42,
                                7,
                                "Resolved",
                                "agent-active; agent-worker:live-ado_worker"
                            ),
                            Encoding.UTF8,
                            "application/json"
                        ),
                    };
                }

                if (req.Method == HttpMethod.Patch)
                {
                    // Simulate 412 Precondition Failed (concurrent modification)
                    return new HttpResponseMessage(HttpStatusCode.PreconditionFailed)
                    {
                        Content = new StringContent(
                            """{"message":"The resource has been modified by another user."}""",
                            Encoding.UTF8,
                            "application/json"
                        ),
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        );

        var client = CreateClientWithHandler(handler);

        var workRef = new ExternalWorkRef
        {
            Source = "AzureDevOpsBoards",
            ExternalId = "42",
            Revision = "7",
        };

        var status = new ExternalWorkStatus
        {
            Status = "New",
            Tags = new[] { "agent-ready" },
            RemovedTags = new[] { "agent-active", "agent-worker:*" },
        };

        // Act
        var ok = await client.UpdateWorkItemStatusAsync(workRef, status, CancellationToken.None);

        // Assert: Returns false for RemovedTags-bearing PATCH on 412 (hard-fail)
        Assert.False(ok);
    }

    [Fact]
    public async Task UpdateWorkItemStatus_NoRemovedTags_412ReturnsTrue_BestEffort()
    {
        // Arrange: fake handler returns 412 on the PATCH, but no RemovedTags
        var handler = new CaptureHttpMessageHandler(
            async (req) =>
            {
                if (req.Method == HttpMethod.Patch)
                {
                    return new HttpResponseMessage(HttpStatusCode.PreconditionFailed)
                    {
                        Content = new StringContent(
                            """{"message":"The resource has been modified."}""",
                            Encoding.UTF8,
                            "application/json"
                        ),
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        );

        var client = CreateClientWithHandler(handler);

        var workRef = new ExternalWorkRef
        {
            Source = "AzureDevOpsBoards",
            ExternalId = "42",
            Revision = "3",
        };

        var status = new ExternalWorkStatus
        {
            Status = "Active",
            // No RemovedTags — status-only projection
        };

        // Act
        var ok = await client.UpdateWorkItemStatusAsync(workRef, status, CancellationToken.None);

        // Assert: Returns true for status-only PATCH on 412 (best-effort)
        Assert.True(ok);
    }

    [Fact]
    public async Task UpdateWorkItemStatus_RemovedTags_NonSuccessNon412_ReturnsFalse()
    {
        // Arrange: fake handler returns 500 on the PATCH
        var handler = new CaptureHttpMessageHandler(
            async (req) =>
            {
                if (req.Method == HttpMethod.Get)
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            WorkItemGetResponse(42, 7, "Resolved", "agent-active"),
                            Encoding.UTF8,
                            "application/json"
                        ),
                    };
                }

                if (req.Method == HttpMethod.Patch)
                {
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError)
                    {
                        Content = new StringContent(
                            """{"message":"Server error"}""",
                            Encoding.UTF8,
                            "application/json"
                        ),
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        );

        var client = CreateClientWithHandler(handler);

        var workRef = new ExternalWorkRef
        {
            Source = "AzureDevOpsBoards",
            ExternalId = "42",
            Revision = "7",
        };

        var status = new ExternalWorkStatus
        {
            Status = "New",
            RemovedTags = new[] { "agent-active" },
        };

        // Act
        var ok = await client.UpdateWorkItemStatusAsync(workRef, status, CancellationToken.None);

        // Assert: Non-success (non-412) always returns false
        Assert.False(ok);
    }

    [Fact]
    public async Task UpdateWorkItemStatus_RemovedTagsWithWildcard_StripsConcreteAgentWorkerTag()
    {
        // Arrange: work item has a concrete agent-worker:live-ado_worker tag
        string? patchBody = null;

        var handler = new CaptureHttpMessageHandler(
            async (req) =>
            {
                if (req.Method == HttpMethod.Get)
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            WorkItemGetResponse(
                                10,
                                3,
                                "Resolved",
                                "agent-active; agent-worker:live-ado_worker; backend"
                            ),
                            Encoding.UTF8,
                            "application/json"
                        ),
                    };
                }

                if (req.Method == HttpMethod.Patch)
                {
                    if (req.Content is not null)
                        patchBody = await req.Content.ReadAsStringAsync();

                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            WorkItemPatchResponse(10, 4),
                            Encoding.UTF8,
                            "application/json"
                        ),
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        );

        var client = CreateClientWithHandler(handler);

        var workRef = new ExternalWorkRef
        {
            Source = "AzureDevOpsBoards",
            ExternalId = "10",
            Revision = "3",
        };

        var status = new ExternalWorkStatus
        {
            Status = "New",
            Tags = new[] { "agent-ready" },
            RemovedTags = new[] { "agent-active", "agent-worker:*" },
        };

        // Act
        var ok = await client.UpdateWorkItemStatusAsync(workRef, status, CancellationToken.None);

        // Assert
        Assert.True(ok);
        Assert.NotNull(patchBody);
        // agent-worker:live-ado_worker should be stripped by the wildcard
        Assert.DoesNotContain("agent-worker:", patchBody);
        Assert.DoesNotContain("agent-active", patchBody);
        // Unrelated tags preserved
        Assert.Contains("backend", patchBody);
        Assert.Contains("agent-ready", patchBody);
    }

    // ──────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────

    /// <summary>
    /// Creates an <see cref="AzureDevOpsBoardsWorkSource"/> wired to a mock client.
    /// </summary>
    private static AzureDevOpsBoardsWorkSource CreateWorkSource(
        IAzureDevOpsBoardsClient mockClient,
        string? project = Project,
        string? activeState = null
    )
    {
        var options = new WorkSourceOptions
        {
            Project = project,
            OrganizationUrl = OrgUrl,
            ActiveState = activeState ?? ActiveState,
        };

        var optionsMonitor = new FakeOptionsMonitor<WorkSourceOptions>(options);
        var scopeFactory = CreateScopeFactory(mockClient);

        return new AzureDevOpsBoardsWorkSource(scopeFactory, optionsMonitor);
    }

    private static WorkSourceEnvironmentProfile ManagedEnvironment(
        string key,
        string project,
        IReadOnlyList<string> states,
        IReadOnlyList<string> tags
    )
    {
        return new WorkSourceEnvironmentProfile
        {
            Key = key,
            DisplayName = key,
            Enabled = true,
            Provider = "AzureDevOpsBoards",
            TagPrefix = "agent",
            OrganizationUrl = $"https://dev.azure.com/{key}",
            Project = project,
            CompletedStates = states,
            PatEnvironmentVariable = "TEST_ADO_PAT",
        };
    }

    private static DelegatingScopeFactory CreateScopeFactory(IAzureDevOpsBoardsClient mockClient)
    {
        var services = new ServiceCollection();
        services.AddSingleton(mockClient);
        var provider = services.BuildServiceProvider();

        return new DelegatingScopeFactory(provider);
    }

    /// <summary>
    /// Creates an <see cref="AzureDevOpsBoardsClient"/> wired to a custom HTTP handler.
    /// </summary>
    private static AzureDevOpsBoardsClient CreateClientWithHandler(HttpMessageHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri(OrgUrl + "/") };

        var options = new AzureDevOpsBoardsOptions
        {
            BaseUrl = OrgUrl,
            Project = Project,
            PersonalAccessToken = "test-pat",
        };

        var authBytes = Encoding.ASCII.GetBytes(":test-pat");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(authBytes)
        );

        return new AzureDevOpsBoardsClient(
            http,
            options,
            NullLogger<AzureDevOpsBoardsClient>.Instance
        );
    }

    /// <summary>
    /// Simple <see cref="IServiceScopeFactory"/> that resolves from a static provider.
    /// </summary>
    private sealed class DelegatingScopeFactory : IServiceScopeFactory
    {
        private readonly IServiceProvider _provider;

        public DelegatingScopeFactory(IServiceProvider provider)
        {
            _provider = provider;
        }

        public IServiceScope CreateScope()
        {
            return new DelegatingScope(_provider);
        }
    }

    /// <summary>
    /// Minimal <see cref="IOptionsMonitor{T}"/> that returns a fixed value.
    /// </summary>
    private sealed class FakeOptionsMonitor<T> : IOptionsMonitor<T>
    {
        private readonly T _value;

        public FakeOptionsMonitor(T value)
        {
            _value = value;
        }

        public T CurrentValue => _value;

        public T Get(string? name) => _value;

        public IDisposable OnChange(Action<T, string?> listener) => EmptyDisposable.Instance;
    }

    private sealed class EmptyDisposable : IDisposable
    {
        public static readonly EmptyDisposable Instance = new();

        private EmptyDisposable() { }

        public void Dispose() { }
    }

    private sealed class DelegatingScope : IServiceScope
    {
        private readonly IServiceProvider _provider;

        public DelegatingScope(IServiceProvider provider)
        {
            _provider = provider;
        }

        public IServiceProvider ServiceProvider => _provider;

        public void Dispose() { }
    }

    private sealed class StubManagedProfileResolver(
        IReadOnlyList<WorkSourceEnvironmentProfile> profiles
    ) : IManagedProfileResolver
    {
        public Task<ResolvedControllerProfiles?> ResolveForRepositoryAsync(
            string repositoryKey,
            CancellationToken cancellationToken
        ) => Task.FromResult<ResolvedControllerProfiles?>(null);

        public Task<ResolvedWorkSourceEnvironment?> ResolveWorkSourceEnvironmentAsync(
            string? key,
            CancellationToken cancellationToken
        )
        {
            var profile = profiles.SingleOrDefault(candidate => candidate.Key == key);
            return Task.FromResult(
                profile is null
                    ? null
                    : new ResolvedWorkSourceEnvironment(profile, IsManaged: true)
            );
        }

        public Task<IReadOnlyList<ResolvedWorkSourceEnvironment>> ListWorkSourceEnvironmentsAsync(
            CancellationToken cancellationToken
        )
        {
            return Task.FromResult<IReadOnlyList<ResolvedWorkSourceEnvironment>>(
                profiles
                    .Select(profile => new ResolvedWorkSourceEnvironment(profile, IsManaged: true))
                    .ToList()
            );
        }
    }

    private sealed class StubClientFactory(
        IReadOnlyDictionary<string, IAzureDevOpsBoardsClient> clients
    ) : IAzureDevOpsBoardsClientFactory
    {
        public IAzureDevOpsBoardsClient Create(WorkSourceEnvironmentProfile profile) =>
            clients[profile.Key];
    }

    /// <summary>
    /// Mock <see cref="IAzureDevOpsBoardsClient"/> that records calls and returns
    /// pre-configured results for <see cref="IAzureDevOpsBoardsClient.UpdateWorkItemStatusAsync"/>
    /// and <see cref="IAzureDevOpsBoardsClient.AddCommentAsync"/>.
    /// </summary>
    private sealed class MockAzureDevOpsBoardsClient : IAzureDevOpsBoardsClient
    {
        public bool UpdateWorkItemStatusAsyncReturns { get; set; } = true;
        public IReadOnlyList<WorkCandidate> QueryResults { get; init; } = [];
        public List<BoardsQueryParameters> QueryCalls { get; } = [];
        public List<(
            ExternalWorkRef WorkRef,
            ExternalWorkStatus Status
        )> UpdateWorkItemStatusCalls { get; } = new();
        public List<string> AddCommentCalls { get; } = new();

        public Task<IReadOnlyList<WorkCandidate>> QueryWorkItemsAsync(
            BoardsQueryParameters parameters,
            CancellationToken ct
        )
        {
            QueryCalls.Add(parameters);
            return Task.FromResult(QueryResults);
        }

        public Task<ClaimResult> TryClaimWorkItemAsync(
            ExternalWorkRef workRef,
            ClaimRequest claim,
            CancellationToken ct
        ) => Task.FromResult(new ClaimResult { Success = false });

        public Task<bool> UpdateWorkItemStatusAsync(
            ExternalWorkRef workRef,
            ExternalWorkStatus status,
            CancellationToken ct
        )
        {
            UpdateWorkItemStatusCalls.Add((workRef, status));
            return Task.FromResult(UpdateWorkItemStatusAsyncReturns);
        }

        public Task AddCommentAsync(ExternalWorkRef workRef, string comment, CancellationToken ct)
        {
            AddCommentCalls.Add(comment);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<WorkItemComment>> GetCommentsAsync(
            ExternalWorkRef workRef,
            int maxComments,
            CancellationToken ct
        ) => Task.FromResult<IReadOnlyList<WorkItemComment>>(Array.Empty<WorkItemComment>());

        public Task<IReadOnlyList<RepositoryInfo>> ListRepositoriesAsync(
            string project,
            CancellationToken ct
        ) => Task.FromResult<IReadOnlyList<RepositoryInfo>>(Array.Empty<RepositoryInfo>());

        public Task ReleaseClaimWorkItemAsync(ReleaseClaimRequest request, CancellationToken ct) =>
            Task.CompletedTask;

        public Task<AzureDevOpsConnectivityResult> VerifyConnectivityAsync(
            string organizationUrl,
            string project,
            string personalAccessToken,
            CancellationToken ct
        ) => Task.FromResult(new AzureDevOpsConnectivityResult { Success = false });

        public Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> GetValidStatesAsync(
            string project,
            CancellationToken ct
        ) => Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<string>>>(
            new Dictionary<string, IReadOnlyList<string>>());

    }

    /// <summary>
    /// Fake <see cref="HttpMessageHandler"/> that delegates to an async callback
    /// for each request, enabling inspection of request headers and body.
    /// </summary>
    private sealed class CaptureHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _handler;

        public CaptureHttpMessageHandler(
            Func<HttpRequestMessage, Task<HttpResponseMessage>> handler
        )
        {
            _handler = handler;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            return await _handler(request);
        }
    }

    /// <summary>
    /// Builds a JSON response for a single work item GET (for claim/tag verification).
    /// </summary>
    private static string WorkItemGetResponse(int id, int rev, string state, string tags)
    {
        return JsonSerializer.Serialize(
            new
            {
                id,
                rev,
                fields = new Dictionary<string, object>
                {
                    ["System.Id"] = id,
                    ["System.Title"] = "Test work item",
                    ["System.State"] = state,
                    ["System.Tags"] = tags,
                },
                _links = new { self = new { href = $"{OrgUrl}/_apis/wit/workItems/{id}" } },
                url = $"{OrgUrl}/_apis/wit/workItems/{id}",
            }
        );
    }

    /// <summary>
    /// Builds a JSON response for a successful work item PATCH.
    /// </summary>
    private static string WorkItemPatchResponse(int id, int rev)
    {
        return JsonSerializer.Serialize(
            new
            {
                id,
                rev,
                fields = new Dictionary<string, object>
                {
                    ["System.Id"] = id,
                    ["System.Rev"] = rev,
                },
                _links = new { self = new { href = $"{OrgUrl}/_apis/wit/workItems/{id}" } },
                url = $"{OrgUrl}/_apis/wit/workItems/{id}",
            }
        );
    }
}

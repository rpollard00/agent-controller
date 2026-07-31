using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using AgentController.Application;
using AgentController.Domain;
using AgentController.Domain.Secrets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentController.Infrastructure.Tests;

public sealed class AzureDevOpsManagedPullRequestDiscoveryTests
{
    private const string OrganizationUrl = "https://dev.azure.com/example";
    private const string TestPat = "test-pat-must-not-leak";

    [Fact]
    public async Task Client_PaginatesAndDeduplicatesActivePullRequests()
    {
        var requestedUris = new List<string>();
        var handler = new DelegateHandler(request =>
        {
            requestedUris.Add(request.RequestUri?.AbsoluteUri ?? string.Empty);
            Assert.Equal("Basic", request.Headers.Authorization?.Scheme);
            Assert.DoesNotContain(TestPat, request.Headers.Authorization?.Parameter ?? string.Empty);

            var skip = ReadSkip(request.RequestUri);
            return JsonResponse(skip switch
            {
                0 => PullRequestPage(PullRequest(1, "First"), PullRequest(2, "Second")),
                2 => PullRequestPage(PullRequest(2, "Second duplicate"), PullRequest(3, "Third")),
                _ => PullRequestPage(),
            });
        });
        using var client = CreateClient(handler, pageSize: 2);

        var snapshots = await client.ListActiveAsync(ManagedRepository(), CancellationToken.None);

        Assert.Equal(["1", "2", "3"], snapshots.Select(snapshot => snapshot.PullRequestId));
        Assert.Equal(3, requestedUris.Count(uri => uri.Contains("/pullrequests?", StringComparison.OrdinalIgnoreCase)));
        Assert.Contains(requestedUris, uri => uri.Contains("%24skip=0", StringComparison.OrdinalIgnoreCase)
            || uri.Contains("$skip=0", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(requestedUris, uri => uri.Contains("%24skip=2", StringComparison.OrdinalIgnoreCase)
            || uri.Contains("$skip=2", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("feature/1", snapshots[0].SourceBranch);
        Assert.Equal("main", snapshots[0].TargetBranch);
        Assert.Equal("commit-1", snapshots[0].SourceCommitSha);
        Assert.Equal("agent-assistance-requested", Assert.Single(snapshots[0].Labels));
        Assert.Equal("101", Assert.Single(snapshots[0].LinkedWorkItems).WorkItemId);
        Assert.DoesNotContain(TestPat, JsonSerializer.Serialize(snapshots));
    }

    [Fact]
    public async Task Client_HydratesLabelsAndLinkedWorkItemsWhenListOmitsThem()
    {
        var requestedUris = new List<string>();
        var handler = new DelegateHandler(request =>
        {
            var uri = request.RequestUri?.AbsoluteUri ?? string.Empty;
            requestedUris.Add(uri);

            if (uri.Contains("/labels?", StringComparison.OrdinalIgnoreCase))
            {
                return JsonResponse("""
                    {"value":[
                      {"name":"agent-assistance-requested"},
                      {"name":"AGENT-ASSISTANCE-REQUESTED"}
                    ]}
                    """);
            }

            if (uri.Contains("/workitems?", StringComparison.OrdinalIgnoreCase))
            {
                return JsonResponse("""
                    {"value":[
                      {"id":"501","url":"https://dev.azure.com/example/_apis/wit/workItems/501"},
                      {"id":"501","url":"https://duplicate.invalid/501"}
                    ]}
                    """);
            }

            return JsonResponse(PullRequestPage(PullRequest(
                9,
                "Needs hydration",
                includeRelationships: false
            )));
        });
        using var client = CreateClient(handler, pageSize: 10);

        var snapshot = Assert.Single(
            await client.ListActiveAsync(ManagedRepository(), CancellationToken.None)
        );

        Assert.Equal("agent-assistance-requested", Assert.Single(snapshot.Labels));
        var workItem = Assert.Single(snapshot.LinkedWorkItems);
        Assert.Equal("501", workItem.WorkItemId);
        Assert.Equal("https://dev.azure.com/example/_apis/wit/workItems/501", workItem.WorkItemUrl);
        Assert.Equal(3, requestedUris.Count);
    }

    [Fact]
    public async Task ActiveClient_RemainsFailSoftWhenRelationshipHydrationFails()
    {
        var handler = new DelegateHandler(request =>
        {
            var uri = request.RequestUri?.AbsoluteUri ?? string.Empty;
            if (uri.Contains("/labels?", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.BadGateway);
            }

            if (uri.Contains("/workitems?", StringComparison.OrdinalIgnoreCase))
            {
                return JsonResponse("{\"value\":{}}");
            }

            return JsonResponse(PullRequestPage(PullRequest(
                9,
                "Needs hydration",
                includeRelationships: false
            )));
        });
        using var client = CreateClient(handler, pageSize: 10);

        var snapshot = Assert.Single(
            await client.ListActiveAsync(ManagedRepository(), CancellationToken.None)
        );

        Assert.Empty(snapshot.Labels);
        Assert.Empty(snapshot.LinkedWorkItems);
    }

    [Fact]
    public async Task Client_ReadsPullRequestLabels()
    {
        var requestedUris = new List<string>();
        var handler = new DelegateHandler(request =>
        {
            requestedUris.Add(request.RequestUri?.AbsoluteUri ?? string.Empty);
            Assert.Equal("Basic", request.Headers.Authorization?.Scheme);
            Assert.DoesNotContain(TestPat, request.Headers.Authorization?.Parameter ?? string.Empty);
            return JsonResponse("""
                {"value":[
                  {"id":"requested-id","name":" agent-assistance-requested "},
                  {"id":42,"name":"keep"},
                  {"name":""}
                ]}
                """);
        });
        using var client = CreateLabelClient(handler);

        var labels = await client.GetLabelsAsync(
            ManagedRepository(),
            "42",
            CancellationToken.None
        );

        Assert.Equal(["agent-assistance-requested", "keep"], labels.Select(label => label.Name));
        Assert.Contains(requestedUris, uri => uri.Contains(
            "Payments%20Project/_apis/git/repositories/payments-id/pullRequests/42/labels?",
            StringComparison.OrdinalIgnoreCase
        ));
    }

    [Fact]
    public async Task Client_LabelRead_SurfacesProviderFailureWithoutCredentials()
    {
        var handler = new DelegateHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        using var client = CreateLabelClient(handler);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetLabelsAsync(
            ManagedRepository(),
            "42",
            CancellationToken.None
        ));

        Assert.Equal(HttpStatusCode.Forbidden, exception.StatusCode);
        Assert.DoesNotContain(TestPat, exception.Message);
    }

    [Fact]
    public async Task Client_LabelRead_SurfacesMalformedResponse()
    {
        var handler = new DelegateHandler(_ => JsonResponse("{\"value\":{}}"));
        using var client = CreateLabelClient(handler);

        await Assert.ThrowsAsync<InvalidDataException>(() => client.GetLabelsAsync(
            ManagedRepository(),
            "42",
            CancellationToken.None
        ));
    }

    [Fact]
    public async Task Client_LabelRead_PropagatesCancellation()
    {
        var handler = new DelegateHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return JsonResponse("{\"value\":[]}");
        });
        using var client = CreateLabelClient(handler);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(25));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetLabelsAsync(
            ManagedRepository(),
            "42",
            cancellation.Token
        ));
    }

    [Fact]
    public async Task Client_LabelMutation_IsCaseInsensitiveAndIdempotent()
    {
        var labels = new List<(string Id, string Name)>
        {
            ("requested-id", "Agent-Assistance-Requested"),
            ("keep-id", "keep"),
        };
        var requests = new List<(HttpMethod Method, string Uri)>();
        var handler = new DelegateHandler(async (request, cancellationToken) =>
        {
            var uri = request.RequestUri?.AbsoluteUri ?? string.Empty;
            requests.Add((request.Method, uri));

            if (request.Method == HttpMethod.Get)
            {
                return JsonResponse(JsonSerializer.Serialize(new
                {
                    value = labels.Select(label => new
                    {
                        id = label.Id,
                        name = label.Name,
                    }),
                }));
            }

            if (request.Method == HttpMethod.Delete)
            {
                var identifier = Uri.UnescapeDataString(
                    request.RequestUri?.Segments[^1].TrimEnd('/') ?? string.Empty
                );
                labels.RemoveAll(label => label.Id.Equals(
                    identifier,
                    StringComparison.OrdinalIgnoreCase
                ));
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            Assert.Equal(HttpMethod.Post, request.Method);
            var payload = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(payload);
            labels.Add(("progress-id", document.RootElement.GetProperty("name").GetString()!));
            return JsonResponse("{\"id\":\"progress-id\",\"name\":\"assistance-in-progress\"}");
        });
        using var client = CreateLabelClient(handler);
        var mutation = new PullRequestLabelMutation
        {
            LabelsToAdd = [" KEEP ", " assistance-in-progress ", "ASSISTANCE-IN-PROGRESS"],
            LabelsToRemove = [" agent-assistance-requested ", "AGENT-ASSISTANCE-REQUESTED"],
        };

        await client.MutateAsync(ManagedRepository(), "42", mutation, CancellationToken.None);
        await client.MutateAsync(ManagedRepository(), "42", mutation, CancellationToken.None);

        Assert.Equal(["keep", "assistance-in-progress"], labels.Select(label => label.Name));
        Assert.Equal(2, requests.Count(request => request.Method == HttpMethod.Get));
        Assert.Single(requests, request => request.Method == HttpMethod.Delete);
        Assert.Single(requests, request => request.Method == HttpMethod.Post);
        Assert.Contains(requests, request =>
            request.Method == HttpMethod.Delete
            && request.Uri.Contains("/labels/requested-id?", StringComparison.OrdinalIgnoreCase)
        );
    }

    [Fact]
    public async Task Client_LabelMutation_DoesNotWriteForCaseInsensitiveExistingState()
    {
        var methods = new List<HttpMethod>();
        var handler = new DelegateHandler(request =>
        {
            methods.Add(request.Method);
            Assert.Equal(HttpMethod.Get, request.Method);
            return JsonResponse("""
                {"value":[{"id":"existing-id","name":"Agent-Assistance-In-Progress"}]}
                """);
        });
        using var client = CreateLabelClient(handler);

        await client.MutateAsync(
            ManagedRepository(),
            "42",
            new PullRequestLabelMutation
            {
                LabelsToAdd = ["agent-assistance-in-progress"],
                LabelsToRemove = ["agent-rework-requested"],
            },
            CancellationToken.None
        );

        Assert.Equal([HttpMethod.Get], methods);
    }

    [Fact]
    public async Task Client_LabelMutation_TreatsConcurrentAlreadyAppliedWritesAsSuccess()
    {
        var handler = new DelegateHandler(request =>
        {
            if (request.Method == HttpMethod.Get)
            {
                return JsonResponse("""
                    {"value":[{"id":"requested-id","name":"agent-assistance-requested"}]}
                    """);
            }

            return new HttpResponseMessage(
                request.Method == HttpMethod.Delete
                    ? HttpStatusCode.NotFound
                    : HttpStatusCode.Conflict
            );
        });
        using var client = CreateLabelClient(handler);

        await client.MutateAsync(
            ManagedRepository(),
            "42",
            new PullRequestLabelMutation
            {
                LabelsToAdd = ["agent-assistance-in-progress"],
                LabelsToRemove = ["agent-assistance-requested"],
            },
            CancellationToken.None
        );
    }

    [Fact]
    public async Task Discovery_EnumeratesAllManagedAdoRepositoriesWithOnePatResolution()
    {
        var repositories = new StubRepositoryStore(
            Repository("alpha", "alpha-id", "Alpha"),
            Repository("beta", null, "Beta") with
            {
                CloneUrl = $"{OrganizationUrl}/Beta/_git/beta-id",
                WebUrl = null,
            },
            Repository("duplicate-beta", "beta-id", "Beta"),
            Repository("unmanaged", "unmanaged-id", "Other") with
            {
                RepositoryHostConnectionKey = null,
            }
        );
        var connection = new ConnectionProfile
        {
            Key = "ado-production",
            Provider = "AzureDevOps",
            Enabled = true,
            Capabilities = [ConnectionCapability.Repositories],
            ProviderSettings = new AzureDevOpsConnectionSettings
            {
                OrganizationUrl = OrganizationUrl,
                PersonalAccessTokenReference = SecretReference.ByName("ado-pat"),
            },
        };
        var patResolver = new StubPatResolver(TestPat);
        var clientFactory = new RecordingClientFactory((request, cancellationToken) =>
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            var id = path.Contains("alpha-id", StringComparison.OrdinalIgnoreCase) ? 11 : 22;
            return Task.FromResult(JsonResponse(PullRequestPage(PullRequest(id, $"PR {id}"))));
        });

        var services = new ServiceCollection();
        services.AddSingleton<IRepositoryStore>(repositories);
        services.AddSingleton<IConnectionStore>(new StubConnectionStore(connection));
        services.AddScoped<AzureDevOpsPatResolver>(_ => patResolver);
        await using var provider = services.BuildServiceProvider();
        var discovery = new AzureDevOpsManagedPullRequestDiscovery(
            provider.GetRequiredService<IServiceScopeFactory>(),
            clientFactory,
            NullLogger<AzureDevOpsManagedPullRequestDiscovery>.Instance
        );

        var snapshots = await discovery.ListActiveAsync(CancellationToken.None);

        Assert.Equal(2, snapshots.Count);
        Assert.Equal(["alpha", "beta"], snapshots.Select(snapshot => snapshot.RepositoryKey));
        Assert.All(snapshots, snapshot => Assert.Equal("ado-production", snapshot.EnvironmentKey));
        Assert.Equal(1, patResolver.ResolveCount);
        Assert.Equal(2, clientFactory.CreateCount);
        Assert.All(clientFactory.ObservedPats, pat => Assert.Equal(TestPat, pat));
        Assert.True(clientFactory.MaximumObservedConcurrency <= 4);
        Assert.Contains(clientFactory.RequestedUris, uri => uri.Contains("alpha-id", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(clientFactory.RequestedUris, uri => uri.Contains("beta-id", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DiagnosticClient_IncludesInactiveStatusesAndRequestLabels()
    {
        var requestedUris = new List<string>();
        var handler = new DelegateHandler(request =>
        {
            requestedUris.Add(request.RequestUri?.AbsoluteUri ?? string.Empty);
            return JsonResponse(PullRequestPage(
                PullRequest(1, "Active"),
                PullRequest(2, "Completed").Replace(
                    "\"status\":\"active\"",
                    "\"status\":\"completed\"",
                    StringComparison.Ordinal
                ),
                PullRequest(3, "Abandoned").Replace(
                    "\"status\":\"active\"",
                    "\"status\":\"abandoned\"",
                    StringComparison.Ordinal
                )
            ));
        });
        using var client = CreateClient(handler, pageSize: 10);

        var snapshots = await client.ListForDiagnosticsAsync(
            ManagedRepository(),
            includeInactive: true,
            CancellationToken.None
        );

        Assert.Equal(["active", "completed", "abandoned"], snapshots.Select(item => item.Status));
        Assert.All(snapshots, item => Assert.Equal(
            "agent-assistance-requested",
            Assert.Single(item.Labels)
        ));
        Assert.Contains(requestedUris, uri => uri.Contains(
            "searchCriteria.status=all",
            StringComparison.OrdinalIgnoreCase
        ));
    }

    [Fact]
    public async Task DiagnosticDiscovery_FiltersEnvironmentsAndReturnsDeterministicPages()
    {
        var repositories = new StubRepositoryStore(
            Repository("zeta", "zeta-id", "Zeta"),
            Repository("alpha", "alpha-id", "Alpha") with
            {
                RepositoryHostConnectionKey = "ado-secondary",
            },
            Repository("beta", "beta-id", "Beta") with
            {
                RepositoryHostConnectionKey = "ado-secondary",
            }
        );
        var clientFactory = new RecordingClientFactory((request, _) =>
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            var id = path.Contains("alpha-id", StringComparison.OrdinalIgnoreCase) ? 1
                : path.Contains("beta-id", StringComparison.OrdinalIgnoreCase) ? 2
                : 3;
            return Task.FromResult(JsonResponse(PullRequestPage(PullRequest(id, $"PR {id}"))));
        });
        var discovery = CreateDiscovery(
            repositories,
            clientFactory,
            Connection("ado-production"),
            Connection("ado-secondary")
        );

        var firstPage = await discovery.ListAsync(
            new ManagedPullRequestDiscoveryQuery
            {
                SourceControlEnvironmentKey = "ADO-SECONDARY",
                Page = 1,
                PageSize = 1,
            },
            CancellationToken.None
        );
        var secondPage = await discovery.ListAsync(
            new ManagedPullRequestDiscoveryQuery
            {
                SourceControlEnvironmentKey = "ado-secondary",
                Page = 2,
                PageSize = 1,
            },
            CancellationToken.None
        );

        Assert.Equal(2, firstPage.Total);
        Assert.Equal("alpha", Assert.Single(firstPage.Items).RepositoryKey);
        Assert.Equal("beta", Assert.Single(secondPage.Items).RepositoryKey);
        Assert.All(firstPage.Items.Concat(secondPage.Items), item =>
            Assert.Equal("ado-secondary", item.EnvironmentKey)
        );
        Assert.DoesNotContain(clientFactory.RequestedUris, uri => uri.Contains(
            "zeta-id",
            StringComparison.OrdinalIgnoreCase
        ));
    }

    [Fact]
    public async Task DiagnosticDiscovery_IsolatesRepositoryHttpFailures()
    {
        var repositories = new StubRepositoryStore(
            Repository("alpha", "alpha-id", "Alpha"),
            Repository("beta", "beta-id", "Beta")
        );
        var clientFactory = new RecordingClientFactory((request, _) =>
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            return Task.FromResult(path.Contains("alpha-id", StringComparison.OrdinalIgnoreCase)
                ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                : JsonResponse(PullRequestPage(PullRequest(2, "Available"))));
        });
        var discovery = CreateDiscovery(
            repositories,
            clientFactory,
            Connection("ado-production")
        );

        var page = await discovery.ListAsync(
            new ManagedPullRequestDiscoveryQuery(),
            CancellationToken.None
        );

        Assert.Equal("beta", Assert.Single(page.Items).RepositoryKey);
        var failure = Assert.Single(page.Failures);
        Assert.Equal("ado-production", failure.SourceControlEnvironmentKey);
        Assert.Equal("alpha", failure.RepositoryKey);
        Assert.Equal("The managed repository could not be queried.", failure.Message);
        Assert.DoesNotContain(TestPat, JsonSerializer.Serialize(page));
    }

    [Theory]
    [InlineData("labels", false)]
    [InlineData("workitems", true)]
    public async Task DiagnosticDiscovery_IsolatesRelationshipHydrationFailures(
        string failingRelationship,
        bool malformedResponse
    )
    {
        var repositories = new StubRepositoryStore(
            Repository("alpha", "alpha-id", "Alpha"),
            Repository("beta", "beta-id", "Beta")
        );
        var clientFactory = new RecordingClientFactory((request, _) =>
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            var isAlpha = path.Contains("alpha-id", StringComparison.OrdinalIgnoreCase);
            if (!isAlpha)
            {
                return Task.FromResult(JsonResponse(PullRequestPage(
                    PullRequest(2, "Available")
                )));
            }

            if ((request.RequestUri?.Query ?? string.Empty).Contains(
                "searchCriteria.status",
                StringComparison.OrdinalIgnoreCase
            ))
            {
                return Task.FromResult(JsonResponse(PullRequestPage(PullRequest(
                    1,
                    "Incomplete",
                    includeRelationships: false
                ))));
            }

            if (path.Contains($"/{failingRelationship}", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(malformedResponse
                    ? JsonResponse("{\"value\":{}}")
                    : new HttpResponseMessage(HttpStatusCode.BadGateway));
            }

            return Task.FromResult(JsonResponse("{\"value\":[]}"));
        });
        var discovery = CreateDiscovery(
            repositories,
            clientFactory,
            Connection("ado-production")
        );

        var page = await discovery.ListAsync(
            new ManagedPullRequestDiscoveryQuery(),
            CancellationToken.None
        );

        Assert.Equal("beta", Assert.Single(page.Items).RepositoryKey);
        var failure = Assert.Single(page.Failures);
        Assert.Equal("ado-production", failure.SourceControlEnvironmentKey);
        Assert.Equal("alpha", failure.RepositoryKey);
        Assert.Equal("The managed repository could not be queried.", failure.Message);
    }

    [Fact]
    public async Task DiagnosticDiscovery_PropagatesCancellation()
    {
        var clientFactory = new RecordingClientFactory(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return JsonResponse(PullRequestPage());
        });
        var discovery = CreateDiscovery(
            new StubRepositoryStore(Repository("alpha", "alpha-id", "Alpha")),
            clientFactory,
            Connection("ado-production")
        );
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(25));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => discovery.ListAsync(
            new ManagedPullRequestDiscoveryQuery(),
            cancellation.Token
        ));
    }

    [Fact]
    public async Task LabelMutator_ResolvesManagedRepositoryAndConnectionCredential()
    {
        var repository = Repository("payments", "payments-id", "Payments");
        var connection = new ConnectionProfile
        {
            Key = "ado-production",
            Provider = "AzureDevOps",
            Enabled = true,
            Capabilities = [ConnectionCapability.Repositories],
            ProviderSettings = new AzureDevOpsConnectionSettings
            {
                OrganizationUrl = OrganizationUrl,
                PersonalAccessTokenReference = SecretReference.ByName("ado-pat"),
            },
        };
        var patResolver = new StubPatResolver(TestPat);
        var clientFactory = new RecordingLabelClientFactory((_, _) =>
            Task.FromResult(JsonResponse("""
                {"value":[{"id":"progress-id","name":"agent-assistance-in-progress"}]}
                """))
        );
        var services = new ServiceCollection();
        services.AddSingleton<IRepositoryStore>(new StubRepositoryStore(repository));
        services.AddSingleton<IConnectionStore>(new StubConnectionStore(connection));
        services.AddScoped<AzureDevOpsPatResolver>(_ => patResolver);
        await using var provider = services.BuildServiceProvider();
        var mutator = new AzureDevOpsPullRequestLabelMutator(
            provider.GetRequiredService<IServiceScopeFactory>(),
            clientFactory
        );

        await mutator.MutateAsync(
            new PullRequestReference
            {
                EnvironmentKey = "ADO-PRODUCTION",
                RepositoryKey = "PAYMENTS",
                PullRequestId = "42",
            },
            new PullRequestLabelMutation
            {
                LabelsToAdd = ["AGENT-ASSISTANCE-IN-PROGRESS"],
            },
            CancellationToken.None
        );

        Assert.Equal(1, patResolver.ResolveCount);
        Assert.Equal(TestPat, Assert.Single(clientFactory.ObservedPats));
        Assert.Contains(clientFactory.RequestedUris, uri =>
            uri.Contains("Payments/_apis/git/repositories/payments-id/pullRequests/42/labels?", StringComparison.OrdinalIgnoreCase)
        );
    }

    [Fact]
    public async Task Client_PropagatesCancellation()
    {
        var handler = new DelegateHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return JsonResponse(PullRequestPage());
        });
        using var client = CreateClient(handler);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(25));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.ListActiveAsync(ManagedRepository(), cancellation.Token)
        );
    }

    private static AzureDevOpsManagedPullRequestDiscovery CreateDiscovery(
        IRepositoryStore repositories,
        AzureDevOpsReposPullRequestClientFactory clientFactory,
        params ConnectionProfile[] connections
    )
    {
        var services = new ServiceCollection();
        services.AddSingleton(repositories);
        services.AddSingleton<IConnectionStore>(new StubConnectionStore(connections));
        services.AddScoped<AzureDevOpsPatResolver>(_ => new StubPatResolver(TestPat));
        var provider = services.BuildServiceProvider();
        return new AzureDevOpsManagedPullRequestDiscovery(
            provider.GetRequiredService<IServiceScopeFactory>(),
            clientFactory,
            NullLogger<AzureDevOpsManagedPullRequestDiscovery>.Instance
        );
    }

    private static ConnectionProfile Connection(string key) => new()
    {
        Key = key,
        Provider = "AzureDevOps",
        Enabled = true,
        Capabilities = [ConnectionCapability.Repositories],
        ProviderSettings = new AzureDevOpsConnectionSettings
        {
            OrganizationUrl = OrganizationUrl,
            PersonalAccessTokenReference = SecretReference.ByName($"{key}-pat"),
        },
    };

    private static AzureDevOpsReposPullRequestClient CreateClient(
        HttpMessageHandler handler,
        int pageSize = 100
    )
    {
        return new AzureDevOpsReposPullRequestClient(
            new HttpClient(handler),
            OrganizationUrl,
            TestPat,
            NullLogger<AzureDevOpsReposPullRequestClient>.Instance,
            pageSize
        );
    }

    private static AzureDevOpsPullRequestLabelClient CreateLabelClient(
        HttpMessageHandler handler
    )
    {
        return new AzureDevOpsPullRequestLabelClient(
            new HttpClient(handler),
            OrganizationUrl,
            TestPat
        );
    }

    private static AzureDevOpsManagedRepository ManagedRepository() => new()
    {
        EnvironmentKey = "ado-production",
        RepositoryKey = "payments",
        Project = "Payments Project",
        RemoteIdentity = "payments-id",
        RepositoryWebUrl = $"{OrganizationUrl}/Payments%20Project/_git/payments",
    };

    private static RepositoryProfile Repository(
        string key,
        string? remoteIdentity,
        string project
    ) => new()
    {
        Key = key,
        CloneUrl = $"{OrganizationUrl}/{project}/_git/{remoteIdentity ?? key}",
        WebUrl = $"{OrganizationUrl}/{project}/_git/{remoteIdentity ?? key}",
        RepositoryHostConnectionKey = "ado-production",
        Project = project,
        RemoteIdentity = remoteIdentity,
    };

    private static int ReadSkip(Uri? uri)
    {
        if (uri is null)
        {
            return 0;
        }

        foreach (var pair in uri.Query.TrimStart('?').Split('&'))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length == 2
                && Uri.UnescapeDataString(parts[0]).Equals("$skip", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(parts[1], out var skip))
            {
                return skip;
            }
        }

        return 0;
    }

    private static string PullRequestPage(params string[] pullRequests) =>
        $"{{\"count\":{pullRequests.Length},\"value\":[{string.Join(',', pullRequests)}]}}";

    private static string PullRequest(
        int id,
        string title,
        bool includeRelationships = true
    )
    {
        var relationships = includeRelationships
            ? "\"labels\":[{\"name\":\"agent-assistance-requested\"}],"
                + $"\"workItemRefs\":[{{\"id\":\"{id + 100}\","
                + $"\"url\":\"https://dev.azure.com/example/_apis/wit/workItems/{id + 100}\"}}],"
            : string.Empty;

        return "{"
            + $"\"pullRequestId\":{id},"
            + "\"status\":\"active\","
            + $"\"title\":\"{title}\","
            + $"\"sourceRefName\":\"refs/heads/feature/{id}\","
            + "\"targetRefName\":\"refs/heads/main\","
            + $"\"lastMergeSourceCommit\":{{\"commitId\":\"commit-{id}\"}},"
            + relationships
            + "\"repository\":{\"name\":\"payments\"},"
            + $"\"_links\":{{\"web\":{{\"href\":\"https://dev.azure.com/example/Payments/_git/payments/pullrequest/{id}\"}}}}"
            + "}";
    }

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

    private sealed class RecordingClientFactory(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responseFactory
    ) : AzureDevOpsReposPullRequestClientFactory
    {
        private int _activeRequests;
        private int _createCount;
        private int _maximumObservedConcurrency;

        public int CreateCount => _createCount;

        public int MaximumObservedConcurrency => _maximumObservedConcurrency;

        public List<string> ObservedPats { get; } = [];

        public ConcurrentBag<string> RequestedUris { get; } = [];

        public override AzureDevOpsReposPullRequestClient Create(
            string organizationUrl,
            string personalAccessToken
        )
        {
            Interlocked.Increment(ref _createCount);
            lock (ObservedPats)
            {
                ObservedPats.Add(personalAccessToken);
            }

            var handler = new DelegateHandler(async (request, cancellationToken) =>
            {
                RequestedUris.Add(request.RequestUri?.AbsoluteUri ?? string.Empty);
                var active = Interlocked.Increment(ref _activeRequests);
                UpdateMaximum(active);
                try
                {
                    await Task.Delay(10, cancellationToken);
                    return await responseFactory(request, cancellationToken);
                }
                finally
                {
                    Interlocked.Decrement(ref _activeRequests);
                }
            });

            return new AzureDevOpsReposPullRequestClient(
                new HttpClient(handler),
                organizationUrl,
                personalAccessToken,
                NullLogger<AzureDevOpsReposPullRequestClient>.Instance
            );
        }

        private void UpdateMaximum(int candidate)
        {
            var observed = Volatile.Read(ref _maximumObservedConcurrency);
            while (candidate > observed)
            {
                var prior = Interlocked.CompareExchange(
                    ref _maximumObservedConcurrency,
                    candidate,
                    observed
                );
                if (prior == observed)
                {
                    return;
                }

                observed = prior;
            }
        }
    }

    private sealed class RecordingLabelClientFactory(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responseFactory
    ) : AzureDevOpsPullRequestLabelClientFactory
    {
        public List<string> ObservedPats { get; } = [];

        public ConcurrentBag<string> RequestedUris { get; } = [];

        public override AzureDevOpsPullRequestLabelClient Create(
            string organizationUrl,
            string personalAccessToken
        )
        {
            ObservedPats.Add(personalAccessToken);
            var handler = new DelegateHandler((request, cancellationToken) =>
            {
                RequestedUris.Add(request.RequestUri?.AbsoluteUri ?? string.Empty);
                return responseFactory(request, cancellationToken);
            });
            return new AzureDevOpsPullRequestLabelClient(
                new HttpClient(handler),
                organizationUrl,
                personalAccessToken
            );
        }
    }

    private sealed class StubPatResolver(string? pat) : AzureDevOpsPatResolver
    {
        private int _resolveCount;

        public int ResolveCount => _resolveCount;

        public override Task<string?> ResolveFromSecretReferenceAsync(
            SecretReference reference,
            CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _resolveCount);
            return Task.FromResult(pat);
        }
    }

    private sealed class StubRepositoryStore(params RepositoryProfile[] repositories)
        : IRepositoryStore
    {
        public Task<IReadOnlyList<RepositoryProfile>> ListAsync(
            CancellationToken cancellationToken
        ) => Task.FromResult<IReadOnlyList<RepositoryProfile>>(repositories);

        public Task<RepositoryProfile?> GetByKeyAsync(
            string key,
            CancellationToken cancellationToken
        ) => Task.FromResult(repositories.FirstOrDefault(repository =>
            repository.Key.Equals(key, StringComparison.OrdinalIgnoreCase)
        ));

        public Task<bool> CreateAsync(
            RepositoryProfile profile,
            CancellationToken cancellationToken
        ) => Task.FromResult(false);

        public Task<bool> UpdateAsync(
            RepositoryProfile profile,
            CancellationToken cancellationToken
        ) => Task.FromResult(false);

        public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task UpsertAsync(
            RepositoryProfile profile,
            CancellationToken cancellationToken
        ) => Task.CompletedTask;
    }

    private sealed class StubConnectionStore(params ConnectionProfile[] connections)
        : IConnectionStore
    {
        public Task<IReadOnlyList<ConnectionProfile>> ListAsync(
            CancellationToken cancellationToken
        ) => Task.FromResult<IReadOnlyList<ConnectionProfile>>(connections);

        public Task<ConnectionProfile?> GetByKeyAsync(
            string key,
            CancellationToken cancellationToken
        ) => Task.FromResult(connections.FirstOrDefault(connection =>
            connection.Key.Equals(key, StringComparison.OrdinalIgnoreCase)
        ));

        public Task<bool> CreateAsync(
            ConnectionProfile profile,
            CancellationToken cancellationToken
        ) => Task.FromResult(false);

        public Task<bool> UpdateAsync(
            ConnectionProfile profile,
            CancellationToken cancellationToken
        ) => Task.FromResult(false);

        public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult(false);
    }
}

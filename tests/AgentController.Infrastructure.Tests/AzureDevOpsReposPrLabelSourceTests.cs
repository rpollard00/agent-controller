using System.Reflection;
using System.Net;
using System.Text;
using AgentController.Application;
using AgentController.Application.Abstractions;
using AgentController.Application.Queries;
using AgentController.Domain;
using AgentController.Domain.Secrets;
using AgentController.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentController.Infrastructure.Tests;

public sealed class AzureDevOpsReposPrLabelSourceTests
{
    private const string OrganizationUrl = "https://dev.azure.com/managed-org";

    [Fact]
    public void Registration_ResolvesLabelSourceWithoutResolvingScopedStores()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAgentControllerAzureDevOpsReposFeedbackSource();

        using var provider = services.BuildServiceProvider(validateScopes: true);

        Assert.IsType<AzureDevOpsReposPrLabelSource>(
            provider.GetRequiredService<IPrLabelSource>()
        );
    }

    [Fact]
    public void Registration_ResolvesFeedbackPipelineAndScopedDiagnosticsHandler()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddApplicationHandlers();
        services.AddAgentControllerFeedbackFilterPipeline();
        services.AddAgentControllerAzureDevOpsReposFeedbackSource();
        AddScopedStoreProxies(services);

        using var provider = services.BuildServiceProvider(validateScopes: true);

        var pipeline = provider.GetRequiredService<ReviewFeedbackFilterPipeline>();
        Assert.Same(pipeline, provider.GetRequiredService<ReviewFeedbackFilterPipeline>());

        using var scope = provider.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<
            IQueryHandler<GetPullRequestDiagnosticsQuery, PullRequestDiagnosticDetail?>
        >();

        Assert.IsType<GetPullRequestDiagnosticsQueryHandler>(handler);
    }

    [Fact]
    public async Task Lookup_UsesManagedRepositoryCoordinatesAndOwningConnection()
    {
        var repository = Repository();
        var connection = Connection();
        var factory = new RecordingLabelClientFactory(
            _ => JsonResponse("{\"value\":[{\"name\":\"agent-rework-requested\"}]}")
        );
        await using var provider = CreateProvider(
            repository,
            connection,
            new FixedPatResolver("fresh-pat")
        );
        var source = CreateSource(provider, factory);

        var labels = await source.GetLabelsAsync(
            new PrUnderTest
            {
                PullRequest = new PullRequestReference
                {
                    EnvironmentKey = connection.Key,
                    RepositoryKey = repository.Key,
                    PullRequestId = "42",
                    PullRequestUrl = "https://untrusted.example/Other/_git/wrong/pullrequest/42",
                },
            },
            CancellationToken.None
        );

        Assert.Equal("agent-rework-requested", Assert.Single(labels).Name);
        Assert.Equal([OrganizationUrl], factory.OrganizationUrls);
        Assert.Equal(["fresh-pat"], factory.PersonalAccessTokens);
        Assert.Contains(factory.RequestedUris, uri => uri.Contains(
            "Payments%20Project/_apis/git/repositories/payments-id/pullRequests/42/labels?",
            StringComparison.OrdinalIgnoreCase
        ));
        Assert.DoesNotContain(factory.RequestedUris, uri => uri.Contains(
            "untrusted.example",
            StringComparison.OrdinalIgnoreCase
        ));
    }

    [Fact]
    public async Task Lookup_ResolvesFreshCredentialForEachOperation()
    {
        var factory = new RecordingLabelClientFactory(
            _ => JsonResponse("{\"value\":[]}")
        );
        await using var provider = CreateProvider(
            Repository(),
            Connection(),
            new RotatingPatResolver()
        );
        var source = CreateSource(provider, factory);
        var pullRequest = new PrUnderTest
        {
            PullRequest = new PullRequestReference
            {
                EnvironmentKey = "ado-production",
                RepositoryKey = "payments",
                PullRequestId = "42",
            },
        };

        await source.GetLabelsAsync(pullRequest, CancellationToken.None);
        await source.GetLabelsAsync(pullRequest, CancellationToken.None);

        Assert.Equal(["pat-1", "pat-2"], factory.PersonalAccessTokens);
    }

    [Fact]
    public async Task Lookup_FailsClosedWhenRepositoryCredentialOrProviderFails()
    {
        await using (var missingRepositoryProvider = CreateProvider(
            repository: null,
            connection: Connection(),
            patResolver: new FixedPatResolver("pat")
        ))
        {
            var source = CreateSource(
                missingRepositoryProvider,
                new RecordingLabelClientFactory(_ => JsonResponse("{\"value\":[]}"))
            );
            var labels = await source.GetLabelsAsync(PullRequest(), CancellationToken.None);
            Assert.Empty(labels);
        }

        await using (var missingCredentialProvider = CreateProvider(
            repository: Repository(),
            connection: Connection(),
            patResolver: new FixedPatResolver(null)
        ))
        {
            var factory = new RecordingLabelClientFactory(_ => JsonResponse("{\"value\":[]}"));
            var source = CreateSource(missingCredentialProvider, factory);
            var labels = await source.GetLabelsAsync(PullRequest(), CancellationToken.None);
            Assert.Empty(labels);
            Assert.Empty(factory.PersonalAccessTokens);
        }

        await using (var providerFailureProvider = CreateProvider(
            repository: Repository(),
            connection: Connection(),
            patResolver: new FixedPatResolver("pat")
        ))
        {
            var source = CreateSource(
                providerFailureProvider,
                new RecordingLabelClientFactory(_ =>
                    new HttpResponseMessage(HttpStatusCode.BadGateway))
            );
            var labels = await source.GetLabelsAsync(PullRequest(), CancellationToken.None);
            Assert.Empty(labels);
        }
    }

    [Fact]
    public async Task Lookup_PropagatesCallerCancellation()
    {
        var factory = new RecordingLabelClientFactory(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return JsonResponse("{\"value\":[]}");
        });
        await using var provider = CreateProvider(
            Repository(),
            Connection(),
            new FixedPatResolver("pat")
        );
        var source = CreateSource(provider, factory);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(25));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.GetLabelsAsync(
            PullRequest(),
            cancellation.Token
        ));
    }

    private static AzureDevOpsReposPrLabelSource CreateSource(
        ServiceProvider provider,
        AzureDevOpsPullRequestLabelClientFactory clientFactory
    ) => new(
        provider.GetRequiredService<IServiceScopeFactory>(),
        clientFactory,
        NullLogger<AzureDevOpsReposPrLabelSource>.Instance
    );

    private static void AddScopedStoreProxies(ServiceCollection services)
    {
        services.AddScoped<IRepositoryStore>(_ => NoOpProxy<IRepositoryStore>());
        services.AddScoped<IAgentRunStore>(_ => NoOpProxy<IAgentRunStore>());
        services.AddScoped<IReworkCycleStore>(_ => NoOpProxy<IReworkCycleStore>());
        services.AddScoped<IReworkFeedbackStore>(_ => NoOpProxy<IReworkFeedbackStore>());
        services.AddScoped<IConnectionStore>(_ => NoOpProxy<IConnectionStore>());
        services.AddScoped<ISecretStore>(_ => NoOpProxy<ISecretStore>());
    }

    private static T NoOpProxy<T>() where T : class =>
        DispatchProxy.Create<T, NoOpDispatchProxy<T>>();

    private static ServiceProvider CreateProvider(
        RepositoryProfile? repository,
        ConnectionProfile connection,
        AzureDevOpsPatResolver patResolver
    )
    {
        var services = new ServiceCollection();
        services.AddSingleton<IRepositoryStore>(new StubRepositoryStore(repository));
        services.AddSingleton<IConnectionStore>(new StubConnectionStore(connection));
        services.AddScoped<AzureDevOpsPatResolver>(_ => patResolver);
        return services.BuildServiceProvider();
    }

    private static PrUnderTest PullRequest() => new()
    {
        PullRequest = new PullRequestReference
        {
            EnvironmentKey = "ado-production",
            RepositoryKey = "payments",
            PullRequestId = "42",
        },
    };

    private static RepositoryProfile Repository() => new()
    {
        Key = "payments",
        CloneUrl = $"{OrganizationUrl}/Payments%20Project/_git/payments-id",
        WebUrl = $"{OrganizationUrl}/Payments%20Project/_git/payments-id",
        RepositoryHostConnectionKey = "ado-production",
        Project = "Payments Project",
        RemoteIdentity = "payments-id",
    };

    private static ConnectionProfile Connection() => new()
    {
        Key = "ado-production",
        Enabled = true,
        Provider = "AzureDevOps",
        ProviderSettings = new AzureDevOpsConnectionSettings
        {
            OrganizationUrl = OrganizationUrl,
            PersonalAccessTokenReference = SecretReference.ByName("ado-pat"),
        },
    };

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance",
        "CA1852:Seal internal types",
        Justification = "DispatchProxy subclasses this type at runtime."
    )]
    private class NoOpDispatchProxy<T> : DispatchProxy where T : class
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => null;
    }

    private sealed class RecordingLabelClientFactory(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responseFactory
    ) : AzureDevOpsPullRequestLabelClientFactory
    {
        public RecordingLabelClientFactory(
            Func<HttpRequestMessage, HttpResponseMessage> responseFactory
        ) : this((request, _) => Task.FromResult(responseFactory(request)))
        {
        }

        public List<string> OrganizationUrls { get; } = [];

        public List<string> PersonalAccessTokens { get; } = [];

        public List<string> RequestedUris { get; } = [];

        public override AzureDevOpsPullRequestLabelClient Create(
            string organizationUrl,
            string personalAccessToken
        )
        {
            OrganizationUrls.Add(organizationUrl);
            PersonalAccessTokens.Add(personalAccessToken);
            return new AzureDevOpsPullRequestLabelClient(
                new HttpClient(new DelegateHandler((request, cancellationToken) =>
                {
                    RequestedUris.Add(request.RequestUri?.AbsoluteUri ?? string.Empty);
                    return responseFactory(request, cancellationToken);
                })),
                organizationUrl,
                personalAccessToken
            );
        }
    }

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler
    ) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => handler(request, cancellationToken);
    }

    private sealed class FixedPatResolver(string? pat) : AzureDevOpsPatResolver
    {
        public override Task<string?> ResolveFromSecretReferenceAsync(
            SecretReference reference,
            CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(pat);
        }
    }

    private sealed class RotatingPatResolver : AzureDevOpsPatResolver
    {
        private int _count;

        public override Task<string?> ResolveFromSecretReferenceAsync(
            SecretReference reference,
            CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Interlocked.Increment(ref _count);
            return Task.FromResult<string?>($"pat-{count}");
        }
    }

    private sealed class StubRepositoryStore(RepositoryProfile? repository) : IRepositoryStore
    {
        public Task<IReadOnlyList<RepositoryProfile>> ListAsync(
            CancellationToken cancellationToken
        ) => Task.FromResult<IReadOnlyList<RepositoryProfile>>(
            repository is null ? [] : [repository]
        );

        public Task<RepositoryProfile?> GetByKeyAsync(
            string key,
            CancellationToken cancellationToken
        ) => Task.FromResult(
            repository?.Key.Equals(key, StringComparison.OrdinalIgnoreCase) == true
                ? repository
                : null
        );

        public Task<bool> CreateAsync(RepositoryProfile profile, CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task<bool> UpdateAsync(RepositoryProfile profile, CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task UpsertAsync(RepositoryProfile profile, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class StubConnectionStore(ConnectionProfile connection) : IConnectionStore
    {
        public Task<IReadOnlyList<ConnectionProfile>> ListAsync(
            CancellationToken cancellationToken
        ) => Task.FromResult<IReadOnlyList<ConnectionProfile>>([connection]);

        public Task<ConnectionProfile?> GetByKeyAsync(
            string key,
            CancellationToken cancellationToken
        ) => Task.FromResult<ConnectionProfile?>(
            connection.Key.Equals(key, StringComparison.OrdinalIgnoreCase)
                ? connection
                : null
        );

        public Task<bool> CreateAsync(ConnectionProfile profile, CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task<bool> UpdateAsync(ConnectionProfile profile, CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult(false);
    }
}

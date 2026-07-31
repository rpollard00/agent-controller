using System.Net;
using System.Text;
using AgentController.Application;
using AgentController.Domain;
using AgentController.Domain.Secrets;
using AgentController.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentController.Infrastructure.Tests;

public sealed class AzureDevOpsReposFeedbackSourceTests
{
    private const string OrganizationUrl = "https://dev.azure.com/managed-org";
    private const string IdentityId = "11111111-1111-1111-1111-111111111111";
    private const string Descriptor = "aad.managed-reviewer";

    [Fact]
    public async Task Poll_UsesManagedCoordinatesAndMapsEveryAuthorAlias()
    {
        var repository = Repository();
        var connection = Connection();
        var factory = new RecordingFeedbackClientFactory(ResponseJson());
        await using var provider = CreateProvider(repository, connection);
        var source = CreateSource(provider, factory);

        var signals = await source.PollAsync(
            new FeedbackQuery
            {
                OpenPrs =
                [
                    new PrUnderTest
                    {
                        PullRequest = new PullRequestReference
                        {
                            EnvironmentKey = connection.Key,
                            RepositoryKey = repository.Key,
                            PullRequestId = "42",
                            // The display URL is intentionally unrelated to the managed target.
                            PullRequestUrl = "https://untrusted.example/Other/_git/wrong/pullrequest/42",
                        },
                    },
                ],
            },
            CancellationToken.None
        );

        var signal = Assert.Single(signals);
        var comment = Assert.Single(Assert.Single(signal.Threads).Comments);
        Assert.Equal("Managed Reviewer", comment.Author);
        Assert.Equal(
            [
                new ReviewerIdentity
                {
                    Kind = AzureDevOpsReviewerIdentityKinds.Email,
                    Value = "reviewer@example.com",
                },
                new ReviewerIdentity
                {
                    Kind = AzureDevOpsReviewerIdentityKinds.IdentityId,
                    Value = IdentityId,
                },
                new ReviewerIdentity
                {
                    Kind = AzureDevOpsReviewerIdentityKinds.Descriptor,
                    Value = Descriptor,
                },
            ],
            comment.AuthorIdentities
        );
        Assert.Equal(repository.Key, signal.PullRequest.RepositoryKey);
        Assert.Equal([OrganizationUrl], factory.OrganizationUrls);
        Assert.Equal(["managed-pat"], factory.PersonalAccessTokens);
        Assert.Contains(
            factory.RequestedUris,
            uri => uri.Contains(
                "Payments%20Project/_apis/git/repositories/payments-id/pullRequests/42/threads?",
                StringComparison.OrdinalIgnoreCase
            )
        );
        Assert.DoesNotContain(
            factory.RequestedUris,
            uri => uri.Contains("untrusted.example", StringComparison.OrdinalIgnoreCase)
        );
    }

    [Theory]
    [InlineData("email", "reviewer@example.com")]
    [InlineData("identityId", IdentityId)]
    [InlineData("descriptor", Descriptor)]
    public async Task Poll_ProducesFeedbackThatQualifiesForEachAllowlistedIdentityKind(
        string identityKind,
        string identityValue
    )
    {
        var repository = Repository();
        var connection = Connection();
        var factory = new RecordingFeedbackClientFactory(ResponseJson());
        await using var provider = CreateProvider(repository, connection);
        var source = CreateSource(provider, factory);
        var pullRequest = new PrUnderTest
        {
            PullRequest = new PullRequestReference
            {
                EnvironmentKey = connection.Key,
                RepositoryKey = repository.Key,
                PullRequestId = "42",
                PullRequestUrl = "https://managed-org.visualstudio.com/Payments%20Project/_git/payments-id/pullrequest/42",
            },
            ReviewerIdentityProvider = "AzureDevOps",
            ReviewerIdentities =
            [new ReviewerIdentity { Kind = identityKind, Value = identityValue }],
        };
        var query = new FeedbackQuery
        {
            OpenPrs = [pullRequest],
        };

        var fetched = await source.PollAsync(query, CancellationToken.None);
        using var pipeline = new ReviewFeedbackFilterPipeline(
            new PresentLabelSource(),
            NullLogger<ReviewFeedbackFilterPipeline>.Instance
        );
        var filtered = await pipeline.FilterAsync(query, fetched, CancellationToken.None);

        var signal = Assert.Single(filtered);
        Assert.Equal(ReviewThreadStatus.Active, Assert.Single(signal.Threads).Status);
        Assert.Equal("Please fix this", Assert.Single(Assert.Single(signal.Threads).Comments).Body);
    }

    private static AzureDevOpsReposFeedbackSource CreateSource(
        ServiceProvider provider,
        AzureDevOpsPullRequestFeedbackClientFactory clientFactory
    ) => new(provider.GetRequiredService<IServiceScopeFactory>(), clientFactory);

    private static ServiceProvider CreateProvider(
        RepositoryProfile repository,
        ConnectionProfile connection
    )
    {
        var services = new ServiceCollection();
        services.AddSingleton<IRepositoryStore>(new StubRepositoryStore(repository));
        services.AddSingleton<IConnectionStore>(new StubConnectionStore(connection));
        services.AddScoped<AzureDevOpsPatResolver>(_ => new FixedPatResolver("managed-pat"));
        return services.BuildServiceProvider();
    }

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

    private static string ResponseJson() => $$"""
        {
          "value": [
            {
              "id": 7,
              "status": "active",
              "publishedDate": "2026-07-31T01:02:03Z",
              "comments": [
                {
                  "content": "Please fix this",
                  "publishedDate": "2026-07-31T01:03:04Z",
                  "parentCommentId": null,
                  "author": {
                    "displayName": "Managed Reviewer",
                    "uniqueName": "reviewer@example.com",
                    "id": "{{IdentityId}}",
                    "descriptor": "{{Descriptor}}"
                  }
                }
              ]
            }
          ]
        }
        """;

    private sealed class PresentLabelSource : IPrLabelSource
    {
        public Task<IReadOnlyList<PrLabel>> GetLabelsAsync(
            PrUnderTest pr,
            CancellationToken cancellationToken
        ) => Task.FromResult<IReadOnlyList<PrLabel>>(
            [new PrLabel { Name = "agent-rework-requested" }]
        );
    }

    private sealed class RecordingFeedbackClientFactory(string responseJson)
        : AzureDevOpsPullRequestFeedbackClientFactory
    {
        public List<string> OrganizationUrls { get; } = [];

        public List<string> PersonalAccessTokens { get; } = [];

        public List<string> RequestedUris { get; } = [];

        public override AzureDevOpsPullRequestFeedbackClient Create(
            string organizationUrl,
            string personalAccessToken
        )
        {
            OrganizationUrls.Add(organizationUrl);
            PersonalAccessTokens.Add(personalAccessToken);
            return new AzureDevOpsPullRequestFeedbackClient(
                new HttpClient(new DelegateHandler((request, _) =>
                {
                    RequestedUris.Add(request.RequestUri?.AbsoluteUri ?? string.Empty);
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            responseJson,
                            Encoding.UTF8,
                            "application/json"
                        ),
                    });
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

    private sealed class StubRepositoryStore(RepositoryProfile repository) : IRepositoryStore
    {
        public Task<IReadOnlyList<RepositoryProfile>> ListAsync(
            CancellationToken cancellationToken
        ) => Task.FromResult<IReadOnlyList<RepositoryProfile>>([repository]);

        public Task<RepositoryProfile?> GetByKeyAsync(
            string key,
            CancellationToken cancellationToken
        ) => Task.FromResult<RepositoryProfile?>(
            repository.Key.Equals(key, StringComparison.OrdinalIgnoreCase)
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

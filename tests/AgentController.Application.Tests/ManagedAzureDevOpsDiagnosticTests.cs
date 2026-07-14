using AgentController.Application.Abstractions;
using AgentController.Application.Queries;
using AgentController.Application.Results;
using AgentController.Domain;

namespace AgentController.Application.Tests;

public sealed class ManagedAzureDevOpsDiagnosticTests
{
    [Fact]
    public async Task ExecuteAsync_UsesEnabledManagedProfileAndResolvesCredentialWithoutReturningIt()
    {
        const string variable = "AGENT_CONTROLLER_MANAGED_DIAGNOSTIC_PAT";
        const string secret = "managed-secret-value";
        var original = Environment.GetEnvironmentVariable(variable);
        Environment.SetEnvironmentVariable(variable, secret);

        try
        {
            var profile = new WorkSourceEnvironmentProfile
            {
                Key = "managed",
                DisplayName = "Managed",
                Enabled = true,
                OrganizationUrl = "https://dev.azure.com/managed",
                Project = "ManagedProject",
                PatEnvironmentVariable = variable,
            };
            var client = new RecordingBoardsClient();
            var handler = new RunAzureDevOpsDiagnosticQueryHandler(
                new StubDiagnosticConfig(),
                new RecordingBoardsClientFactory(client),
                new StubResolver(profile)
            );

            var result = await handler.ExecuteAsync(
                new RunAzureDevOpsDiagnosticQuery("managed"),
                CancellationToken.None
            );

            Assert.Equal("Connected", result.Status);
            Assert.Equal(profile.OrganizationUrl, result.OrganizationUrl);
            Assert.Equal(profile.Project, result.Project);
            Assert.Equal(secret, client.ReceivedPat);
            Assert.DoesNotContain(
                secret,
                System.Text.Json.JsonSerializer.Serialize(result),
                StringComparison.Ordinal
            );
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, original);
        }
    }

    private sealed class StubResolver(WorkSourceEnvironmentProfile profile)
        : IManagedProfileResolver
    {
        public Task<ResolvedControllerProfiles?> ResolveForRepositoryAsync(
            string repositoryKey,
            CancellationToken cancellationToken
        ) => Task.FromResult<ResolvedControllerProfiles?>(null);

        public Task<ResolvedWorkSourceEnvironment?> ResolveWorkSourceEnvironmentAsync(
            string? key,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult<ResolvedWorkSourceEnvironment?>(
                new ResolvedWorkSourceEnvironment(profile, IsManaged: true)
            );

        public Task<IReadOnlyList<ResolvedWorkSourceEnvironment>> ListWorkSourceEnvironmentsAsync(
            CancellationToken cancellationToken
        ) =>
            Task.FromResult<IReadOnlyList<ResolvedWorkSourceEnvironment>>([
                new ResolvedWorkSourceEnvironment(profile, IsManaged: true),
            ]);
    }

    private sealed class StubDiagnosticConfig : IAzureDevOpsDiagnosticConfig
    {
        public string? OrganizationUrl => "https://dev.azure.com/configured";
        public string? Project => "ConfiguredProject";

        public string? ResolvePersonalAccessToken() => "configured-secret";
    }

    private sealed class RecordingBoardsClientFactory(IAzureDevOpsBoardsClient client)
        : IAzureDevOpsBoardsClientFactory
    {
        public IAzureDevOpsBoardsClient Create(WorkSourceEnvironmentProfile profile) => client;
    }

    private sealed class RecordingBoardsClient : IAzureDevOpsBoardsClient
    {
        public string? ReceivedPat { get; private set; }

        public Task<AzureDevOpsConnectivityResult> VerifyConnectivityAsync(
            string organizationUrl,
            string project,
            string personalAccessToken,
            CancellationToken cancellationToken
        )
        {
            ReceivedPat = personalAccessToken;
            return Task.FromResult(
                new AzureDevOpsConnectivityResult
                {
                    Success = true,
                    Status = System.Net.HttpStatusCode.OK,
                }
            );
        }

        public Task<IReadOnlyList<WorkCandidate>> QueryWorkItemsAsync(
            BoardsQueryParameters parameters,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<ClaimResult> TryClaimWorkItemAsync(
            ExternalWorkRef workRef,
            ClaimRequest request,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<bool> UpdateWorkItemStatusAsync(
            ExternalWorkRef workRef,
            ExternalWorkStatus status,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task AddCommentAsync(
            ExternalWorkRef workRef,
            string comment,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<IReadOnlyList<RepositoryInfo>> ListRepositoriesAsync(
            string project,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<IReadOnlyList<WorkItemComment>> GetCommentsAsync(
            ExternalWorkRef workRef,
            int maxComments,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task ReleaseClaimWorkItemAsync(
            ReleaseClaimRequest request,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<IReadOnlyList<string>> GetValidStatesAsync(
            string project,
            string workItemType,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();
    }
}

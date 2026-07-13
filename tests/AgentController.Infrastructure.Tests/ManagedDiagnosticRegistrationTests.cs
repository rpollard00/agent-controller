using AgentController.Application;
using AgentController.Application.Abstractions;
using AgentController.Application.Queries;
using AgentController.Application.Results;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgentController.Infrastructure.Tests;

public sealed class ManagedDiagnosticRegistrationTests
{
    [Fact]
    public void DiagnosticHandler_ResolvesWithoutConfiguredBoardsClient()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["workSource:provider"] = "AzureDevOpsBoards",
                    ["environmentProvider:provider"] = "NoOp",
                    ["runtime:provider"] = "NoOp",
                }
            )
            .Build();
        var services = new ServiceCollection();
        services.AddSilentLogging();
        services.AddAgentControllerOptions(configuration);
        services.AddAgentControllerLifecycleService();
        services.AddApplicationHandlers();
        services.AddScoped<IManagedProfileResolver, StubManagedProfileResolver>();

        using var serviceProvider = services.BuildServiceProvider();
        using var scope = serviceProvider.CreateScope();

        Assert.NotNull(
            scope.ServiceProvider.GetRequiredService<
                IQueryHandler<RunAzureDevOpsDiagnosticQuery, AzureDevOpsDiagnosticResult>
            >()
        );
        Assert.Null(scope.ServiceProvider.GetService<IAzureDevOpsBoardsClient>());
    }

    private sealed class StubManagedProfileResolver : IManagedProfileResolver
    {
        public Task<ResolvedControllerProfiles?> ResolveForRepositoryAsync(
            string repositoryKey,
            CancellationToken cancellationToken
        ) => Task.FromResult<ResolvedControllerProfiles?>(null);

        public Task<ResolvedAzureDevOpsEnvironment?> ResolveAzureDevOpsEnvironmentAsync(
            string? key,
            CancellationToken cancellationToken
        ) => Task.FromResult<ResolvedAzureDevOpsEnvironment?>(null);

        public Task<IReadOnlyList<ResolvedAzureDevOpsEnvironment>> ListAzureDevOpsEnvironmentsAsync(
            CancellationToken cancellationToken
        ) => Task.FromResult<IReadOnlyList<ResolvedAzureDevOpsEnvironment>>([]);
    }
}

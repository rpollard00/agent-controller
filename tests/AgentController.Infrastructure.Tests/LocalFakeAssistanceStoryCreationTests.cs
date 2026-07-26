using AgentController.Application;
using AgentController.Domain;
using AgentController.Infrastructure.Data;
using AgentController.Infrastructure.Data.Repositories;
using AgentController.Infrastructure.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AgentController.Infrastructure.Tests;

public sealed class LocalFakeAssistanceStoryCreationTests
{
    [Fact]
    public async Task CreateAssistanceStoryAsync_PersistsReadyReworkCandidate()
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            $"agent-controller-assistance-{Guid.NewGuid():N}.db"
        );
        try
        {
            var services = new ServiceCollection();
            services.AddDbContext<AgentControllerDbContext>(options =>
                options.UseSqlite($"Data Source={databasePath}")
            );
            services.AddScoped<IWorkItemStore, EfWorkItemStore>();
            await using var provider = services.BuildServiceProvider();
            await using (var setupScope = provider.CreateAsyncScope())
            {
                await setupScope.ServiceProvider
                    .GetRequiredService<AgentControllerDbContext>()
                    .Database.EnsureCreatedAsync();
            }

            var options = new StaticOptionsMonitor<WorkSourceOptions>(
                new WorkSourceOptions
                {
                    Provider = "LocalFake",
                    TagPrefix = "custom",
                }
            );
            var source = new LocalFakeWorkSource(
                provider.GetRequiredService<IServiceScopeFactory>(),
                options
            );

            var result = await source.CreateAssistanceStoryAsync(
                new CreateAssistanceStoryRequest
                {
                    EnvironmentKey = "local-environment",
                    RepoKey = "widgets",
                    Title = "Continue existing PR",
                    Description = "<p>Use the existing branch.</p>",
                    CorrelationTags = ["assistance:local-cycle-1"],
                },
                CancellationToken.None
            );

            Assert.StartsWith("local-assistance-", result.ExternalId, StringComparison.Ordinal);
            Assert.Equal("1", result.Revision);
            Assert.Equal(result.ExternalId, result.Candidate.ExternalId);
            Assert.Equal("LocalFake", result.Candidate.Source);
            Assert.Equal("widgets", result.Candidate.RepoKey);
            Assert.Equal(
                ["repo:widgets", "custom-ready-rework", "assistance:local-cycle-1"],
                result.Candidate.Tags
            );
            Assert.Equal("1", result.Candidate.SourceMetadata?["revision"]);
            Assert.Equal(
                "local-environment",
                result.Candidate.SourceMetadata?["workSourceEnvironmentKey"]
            );

            await using var verificationScope = provider.CreateAsyncScope();
            var persisted = await verificationScope.ServiceProvider
                .GetRequiredService<IWorkItemStore>()
                .GetByIdAsync(result.Candidate.Id, CancellationToken.None);
            Assert.NotNull(persisted);
            Assert.Equal(result.Candidate.Id, persisted.Id);
            Assert.Equal(result.Candidate.ExternalId, persisted.ExternalId);
            Assert.Equal(result.Candidate.Title, persisted.Title);
            Assert.Equal(result.Candidate.Description, persisted.Description);
            Assert.Equal(result.Candidate.Tags, persisted.Tags);
            Assert.Equal(
                result.Candidate.SourceMetadata,
                persisted.SourceMetadata
            );
        }
        finally
        {
            if (File.Exists(databasePath))
            {
                File.Delete(databasePath);
            }
        }
    }

    private sealed class StaticOptionsMonitor<T>(T currentValue) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = currentValue;

        public T Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}

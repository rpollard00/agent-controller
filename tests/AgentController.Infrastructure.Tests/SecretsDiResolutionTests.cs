using AgentController.Domain.Secrets;
using AgentController.Infrastructure.Secrets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentController.Infrastructure.Tests;

/// <summary>
/// Guard the DI fix: ISecretManager and ISecretStore must resolve to
/// DbNamedSecretProvider (the DB-backed, envelope-encrypted implementation),
/// never to InMemorySecretStore (the in-memory test double).
/// </summary>
public sealed class SecretsDiResolutionTests
{
    /// <summary>
    /// ISecretManager resolves to DbNamedSecretProvider, not InMemorySecretStore.
    /// This guards against the regression where a fallback InMemorySecretStore
    /// registration would silently mask a missing KEK.
    /// </summary>
    [Fact]
    public void Resolve_ISecretManager_IsDbNamedSecretProvider()
    {
        // Arrange — configure with a valid KEK so the Db provider path succeeds.
        var (services, config) = SetupServicesWithValidKek();

        // Act — build and resolve.
        var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();
        var secretManager = scope.ServiceProvider.GetRequiredService<ISecretManager>();

        // Assert — concrete type is DbNamedSecretProvider, not InMemorySecretStore.
        Assert.IsType<DbNamedSecretProvider>(secretManager);
        Assert.NotSame(typeof(InMemorySecretStore), secretManager.GetType());
    }

    /// <summary>
    /// ISecretStore resolves to DbNamedSecretProvider, not InMemorySecretStore.
    /// </summary>
    [Fact]
    public void Resolve_ISecretStore_IsDbNamedSecretProvider()
    {
        // Arrange — configure with a valid KEK so the Db provider path succeeds.
        var (services, config) = SetupServicesWithValidKek();

        // Act — build and resolve.
        var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();
        var secretStore = scope.ServiceProvider.GetRequiredService<ISecretStore>();

        // Assert — concrete type is DbNamedSecretProvider, not InMemorySecretStore.
        Assert.IsType<DbNamedSecretProvider>(secretStore);
        Assert.NotSame(typeof(InMemorySecretStore), secretStore.GetType());
    }

    // ─── Helpers ───────────────────────────────────────────────

    private static (IServiceCollection Services, IConfiguration Config) SetupServicesWithValidKek()
    {
        // Create a deterministic 32-byte KEK file.
        var kekFilePath = Path.Combine(Path.GetTempPath(), $"test-kek-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(kekFilePath, new byte[32]);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["secrets:provider"] = "Db",
                ["secrets:keyEncryptionKey:file:filePath"] = kekFilePath,
                // Minimal persistence config so AddAgentControllerDbContext works.
                ["persistence:provider"] = "Sqlite",
                ["persistence:connectionString"] = "Filename=:memory:",
            })
            .Build();

        var services = new ServiceCollection();

        // Logging is required because RegisterDbNamedSecretProvider resolves ILoggerFactory.
        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.SetMinimumLevel(LogLevel.Warning);
        });

        // DbContext is required because DbNamedSecretProvider depends on it.
        services.AddAgentControllerDbContext(config);

        // Register the named secrets infrastructure (KEK-gated).
        services.AddAgentControllerNamedSecrets(config);

        return (services, config);
    }
}

using AgentController.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentController.Infrastructure.Secrets;

/// <summary>
/// Ensures the secrets tables (NamedSecrets, SecretVersions) exist at startup.
///
/// This service is registered only when the Db secret provider is active and
/// a KEK is configured (i.e., after the KEK check in RegisterDbNamedSecretProvider passes).
/// It runs once during application startup before any secret writes occur.
/// </summary>
internal sealed partial class SecretsDatabaseStartupService : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SecretsDatabaseStartupService> _logger;

    public SecretsDatabaseStartupService(
        IServiceScopeFactory scopeFactory,
        ILogger<SecretsDatabaseStartupService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AgentControllerDbContext>();

        EnsuringTables(_logger);

        try
        {
            await context.Database.EnsureCreatedAsync(cancellationToken);
            TablesVerified(_logger);
        }
        catch (Exception ex)
        {
            TableCreationFailed(_logger, ex.Message);
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    // ─── LoggerMessage definitions ───────────────────────────────

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Ensuring secrets database tables exist...")]
    private static partial void EnsuringTables(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Secrets database tables verified.")]
    private static partial void TablesVerified(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Failed to ensure secrets database tables exist: {Error}. " +
                  "Secret persistence will fail.")]
    private static partial void TableCreationFailed(ILogger logger, string error);
}

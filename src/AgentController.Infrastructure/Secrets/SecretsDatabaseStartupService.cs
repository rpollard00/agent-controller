using AgentController.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentController.Infrastructure.Secrets;

/// <summary>
/// Applies pending EF Core migrations for the secrets tables (NamedSecrets, SecretVersions)
/// at startup so the schema is versioned and migration history is tracked.
///
/// This service is registered only when the Db secret provider is active and
/// a KEK is configured (i.e., after the KEK check in RegisterDbNamedSecretProvider passes).
/// It runs once during application startup before any secret writes occur.
/// Uses MigrateAsync() (not EnsureCreatedAsync()) so that schema changes are
/// versioned through the AgentController.Migrations assembly.
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

        ApplyingMigrations(_logger);

        try
        {
            await context.Database.MigrateAsync(cancellationToken);
            MigrationsApplied(_logger);
        }
        catch (Exception ex)
        {
            MigrationFailed(_logger, ex.Message);
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    // ─── LoggerMessage definitions ───────────────────────────────

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Applying pending secrets database migrations...")]
    private static partial void ApplyingMigrations(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Secrets database migrations applied.")]
    private static partial void MigrationsApplied(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Failed to apply secrets database migrations: {Error}. " +
                  "Secret persistence will fail.")]
    private static partial void MigrationFailed(ILogger logger, string error);
}

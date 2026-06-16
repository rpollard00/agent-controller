using AgentController.Infrastructure.Data;
using AgentController.Infrastructure.Options;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentController.Migrations;

/// <summary>
/// Dedicated database migration runner console application.
///
/// Loads normal application configuration, constructs the Infrastructure
/// <see cref="AgentControllerDbContext"/>, and runs
/// <see cref="DatabaseFacade.MigrateAsync"/> to apply all pending EF Core
/// migrations.
///
/// The API and worker projects register the DbContext for normal operation
/// but <em>never</em> call <c>MigrateAsync()</c> or <c>EnsureCreated()</c>
/// automatically. Schema changes are owned exclusively by this console app
/// to avoid accidental drift or startup race conditions.
/// </summary>
internal static partial class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var host = Host.CreateDefaultBuilder(args)
            .ConfigureAppConfiguration((context, config) =>
            {
                config
                    .SetBasePath(Directory.GetCurrentDirectory())
                    .AddJsonFile("appsettings.json", optional: true)
                    .AddJsonFile("appsettings.Development.json", optional: true)
                    .AddEnvironmentVariables();
            })
            .ConfigureServices((context, services) =>
            {
                // Bind persistence options — same path as the API/worker
                var persistenceOptions = context.Configuration
                    .GetSection(PersistenceOptions.SectionName)
                    .Get<PersistenceOptions>()
                    ?? new PersistenceOptions();

                // Resolve and validate the connection string via the shared resolver.
                // This rejects missing configuration and relative SQLite paths.
                var connectionString =
                    PersistenceConnectionResolver.Resolve(persistenceOptions);

                services.AddDbContext<AgentControllerDbContext>(options =>
                {
                    options.UseSqlite(
                        connectionString,
                        sqliteOptions => sqliteOptions.MigrationsAssembly("AgentController.Migrations")
                    );
                });
            })
            .Build();

        var loggerFactory = host.Services.GetRequiredService<ILoggerFactory>();
        var logger = loggerFactory.CreateLogger("AgentController.Migrations");

        Log.MigrationStarting(logger, DateTimeOffset.UtcNow);

        try
        {
            var dbContext = host.Services.GetRequiredService<AgentControllerDbContext>();

            // Ensure the parent directory exists for file-based SQLite databases
            // so MigrateAsync does not fail because the directory is missing.
            EnsureDatabaseDirectory(dbContext, loggerFactory.CreateLogger("AgentController.Migrations"));

            Log.ApplyingMigrations(logger);
            await dbContext.Database.MigrateAsync();

            Log.MigrationSucceeded(logger, DateTimeOffset.UtcNow);

            return 0;
        }
        catch (Exception ex)
        {
            Log.MigrationFailed(logger, ex, ex.Message);
            return 1;
        }
    }

    /// <summary>
    /// Creates the parent directory for SQLite file-based databases when needed.
    /// In-memory databases are skipped.
    /// </summary>
    private static void EnsureDatabaseDirectory(
        AgentControllerDbContext dbContext,
        ILogger logger)
    {
        var connectionString = dbContext.Database.GetConnectionString();
        if (string.IsNullOrWhiteSpace(connectionString))
            return;

        var createdDir = SqliteDatabaseEnsurer.EnsureDirectory(connectionString);
        if (createdDir is not null)
        {
            Log.DirectoryCreated(logger, createdDir);
        }
    }

    private static partial class Log
    {
        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Starting database migration run at {Timestamp}")]
        public static partial void MigrationStarting(ILogger logger, DateTimeOffset timestamp);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Applying pending EF Core migrations...")]
        public static partial void ApplyingMigrations(ILogger logger);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Migration run completed successfully at {Timestamp}")]
        public static partial void MigrationSucceeded(ILogger logger, DateTimeOffset timestamp);

        [LoggerMessage(
            Level = LogLevel.Critical,
            Message = "Migration run failed: {ErrorMessage}")]
        public static partial void MigrationFailed(ILogger logger, Exception ex, string errorMessage);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Created database directory: {Directory}")]
        public static partial void DirectoryCreated(ILogger logger, string directory);
    }
}

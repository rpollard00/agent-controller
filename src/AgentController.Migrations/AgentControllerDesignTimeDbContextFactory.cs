using AgentController.Infrastructure.Data;
using AgentController.Infrastructure.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace AgentController.Migrations;

/// <summary>
/// EF Core design-time <see cref="AgentControllerDbContext"/> factory.
/// Used by <c>dotnet ef</c> commands to instantiate the DbContext without
/// a running host. Reads the connection string from configuration files
/// (appsettings.json, appsettings.Development.json, environment variables).
///
/// EF Core discovers design-time factories via <c>Assembly.GetTypes()</c>, which
/// includes internal types. The Migrations project has <c>InternalsVisibleTo</c>
/// access to Infrastructure internals, allowing it to reference
/// <see cref="AgentControllerDbContext"/>.
/// </summary>
internal sealed class AgentControllerDesignTimeDbContextFactory
    : IDesignTimeDbContextFactory<AgentControllerDbContext>
{
    public AgentControllerDbContext CreateDbContext(string[] args)
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var persistenceOptions = configuration
            .GetSection(PersistenceOptions.SectionName)
            .Get<PersistenceOptions>();

        var connectionString = persistenceOptions?.ConnectionString
            ?? "Data Source=agent-controller.db";

        var optionsBuilder = new DbContextOptionsBuilder<AgentControllerDbContext>();
        optionsBuilder.UseSqlite(
            connectionString,
            sqliteOptions => sqliteOptions.MigrationsAssembly("AgentController.Migrations")
        );

        return new AgentControllerDbContext(optionsBuilder.Options);
    }
}

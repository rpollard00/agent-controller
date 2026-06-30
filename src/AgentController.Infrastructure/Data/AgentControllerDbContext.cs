using AgentController.Infrastructure.Data.Configurations;
using AgentController.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace AgentController.Infrastructure.Data;

/// <summary>
/// EF Core database context for the Agent Work Controller.
/// Owns all five Phase 1 tables: WorkItems, AgentRuns, Environments,
/// LifecycleEvents, and Repositories.
///
/// Entity configurations are applied via
/// <see cref="OnModelCreating(ModelBuilder)"/> using
/// <see cref="IEntityTypeConfiguration{TEntity}"/> classes.
///
/// This context and its entities are <c>internal</c> to the Infrastructure
/// layer. API and worker code access persistence through application-layer
/// store interfaces (<see cref="Application.IWorkItemStore"/>,
/// <see cref="Application.IAgentRunStore"/>, etc.) and must never reference
/// EF Core types directly.
/// </summary>
internal sealed class AgentControllerDbContext : DbContext
{
    public AgentControllerDbContext(DbContextOptions<AgentControllerDbContext> options)
        : base(options)
    {
    }

    public DbSet<WorkItemEntity> WorkItems => Set<WorkItemEntity>();
    public DbSet<AgentRunEntity> AgentRuns => Set<AgentRunEntity>();
    public DbSet<EnvironmentEntity> Environments => Set<EnvironmentEntity>();
    public DbSet<LifecycleEventEntity> LifecycleEvents => Set<LifecycleEventEntity>();
    public DbSet<RepositoryEntity> Repositories => Set<RepositoryEntity>();
    public DbSet<ReworkCycleEntity> ReworkCycles => Set<ReworkCycleEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new WorkItemEntityConfiguration());
        modelBuilder.ApplyConfiguration(new AgentRunEntityConfiguration());
        modelBuilder.ApplyConfiguration(new EnvironmentEntityConfiguration());
        modelBuilder.ApplyConfiguration(new LifecycleEventEntityConfiguration());
        modelBuilder.ApplyConfiguration(new RepositoryEntityConfiguration());
        modelBuilder.ApplyConfiguration(new ReworkCycleEntityConfiguration());
    }
}

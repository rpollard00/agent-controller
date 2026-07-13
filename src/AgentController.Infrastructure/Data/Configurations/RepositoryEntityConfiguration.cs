using AgentController.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AgentController.Infrastructure.Data.Configurations;

/// <summary>
/// EF Core entity type configuration for <see cref="RepositoryEntity"/>.
/// </summary>
internal sealed class RepositoryEntityConfiguration : IEntityTypeConfiguration<RepositoryEntity>
{
    public void Configure(EntityTypeBuilder<RepositoryEntity> builder)
    {
        builder.ToTable("Repositories");

        builder.HasKey(x => x.Key);

        builder.Property(x => x.Key).HasMaxLength(256);

        builder.Property(x => x.CloneUrl).IsRequired().HasMaxLength(2048);

        builder.Property(x => x.DefaultBranch).IsRequired().HasMaxLength(256);

        builder.Property(x => x.Transport).IsRequired();

        builder.Property(x => x.EnvironmentProfile).IsRequired().HasMaxLength(128);

        builder.Property(x => x.RuntimeProfile).IsRequired().HasMaxLength(128);

        builder.Property(x => x.AzureDevOpsEnvironmentKey).HasMaxLength(128);

        builder.Property(x => x.RuntimeEnvironmentKey).HasMaxLength(128);

        // JSON-like column: stored as TEXT
        builder.Property(x => x.AllowedPathsJson).HasColumnName("AllowedPathsJson");

        // Timestamps
        builder.Property(x => x.CreatedAt).IsRequired();

        builder.Property(x => x.UpdatedAt).IsRequired();
    }
}

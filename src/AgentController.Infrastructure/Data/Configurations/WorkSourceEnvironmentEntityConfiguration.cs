using AgentController.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AgentController.Infrastructure.Data.Configurations;

/// <summary>
/// EF Core configuration for managed work-source environment profiles.
/// Uses <see cref="ConnectionEntityConfigurationHelper"/> for shared connection fields.
/// </summary>
internal sealed class WorkSourceEnvironmentEntityConfiguration
    : IEntityTypeConfiguration<WorkSourceEnvironmentEntity>
{
    public void Configure(EntityTypeBuilder<WorkSourceEnvironmentEntity> builder)
    {
        builder.ToTable("WorkSourceEnvironments");

        builder.HasKey(x => x.Key);

        // Apply common connection entity configurations (Key, DisplayName, Enabled,
        // Provider, OrganizationUrl, Project, CreatedAt, UpdatedAt).
        ConnectionEntityConfigurationHelper.ApplyCommonConfigurations(builder);

        // Work-source-specific fields.
        builder.Property(x => x.TagPrefix)
            .IsRequired()
            .HasMaxLength(64);

        builder.Property(x => x.ActiveState)
            .HasMaxLength(256);

        builder.Property(x => x.CompletedState)
            .HasMaxLength(256);

        builder.Property(x => x.PatEnvironmentVariable)
            .IsRequired()
            .HasMaxLength(256);
    }
}

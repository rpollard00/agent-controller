using AgentController.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AgentController.Infrastructure.Data.Configurations;

/// <summary>
/// EF Core configuration for managed work-source environment profiles.
/// Organization URL and PAT live on the referenced connection; this entity
/// carries only ConnectionKey, consumer-level Project, and board-usage settings.
/// </summary>
internal sealed class WorkSourceEnvironmentEntityConfiguration
    : IEntityTypeConfiguration<WorkSourceEnvironmentEntity>
{
    public void Configure(EntityTypeBuilder<WorkSourceEnvironmentEntity> builder)
    {
        builder.ToTable("WorkSourceEnvironments");

        builder.HasKey(x => x.Key);

        // Common fields from BaseConnectionEntity (Key, DisplayName, Enabled, Provider,
        // CreatedAt, UpdatedAt). OrganizationUrl is excluded — it lives on the connection.
        builder.Property(x => x.Key).HasMaxLength(128);

        builder.Property(x => x.DisplayName)
            .IsRequired()
            .HasMaxLength(256);

        builder.Property(x => x.Enabled).IsRequired();

        builder.Property(x => x.Provider)
            .IsRequired()
            .HasMaxLength(128);

        builder.Property(x => x.CreatedAt).IsRequired();

        builder.Property(x => x.UpdatedAt).IsRequired();

        // Connection reference.
        builder.Property(x => x.ConnectionKey)
            .IsRequired()
            .HasMaxLength(128);

        // Consumer-level project (not the base OrganizationUrl/Project).
        builder.Property(x => x.Project)
            .IsRequired()
            .HasMaxLength(256);

        // Work-source-specific fields.
        builder.Property(x => x.TagPrefix)
            .IsRequired()
            .HasMaxLength(64);

        builder.Property(x => x.ActiveState)
            .HasMaxLength(256);

        builder.Property(x => x.CompletedState)
            .HasMaxLength(256);
    }
}

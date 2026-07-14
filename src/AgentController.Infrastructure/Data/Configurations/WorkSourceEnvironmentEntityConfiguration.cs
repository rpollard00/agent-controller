using AgentController.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AgentController.Infrastructure.Data.Configurations;

/// <summary>
/// EF Core configuration for managed work-source environment profiles.
/// </summary>
internal sealed class WorkSourceEnvironmentEntityConfiguration
    : IEntityTypeConfiguration<WorkSourceEnvironmentEntity>
{
    public void Configure(EntityTypeBuilder<WorkSourceEnvironmentEntity> builder)
    {
        builder.ToTable("WorkSourceEnvironments");

        builder.HasKey(x => x.Key);

        builder.Property(x => x.Key)
            .HasMaxLength(128);

        builder.Property(x => x.DisplayName)
            .IsRequired()
            .HasMaxLength(256);

        builder.Property(x => x.Enabled)
            .IsRequired();

        builder.Property(x => x.Provider)
            .IsRequired()
            .HasMaxLength(128);

        builder.Property(x => x.TagPrefix)
            .IsRequired()
            .HasMaxLength(64);

        builder.Property(x => x.OrganizationUrl)
            .IsRequired()
            .HasMaxLength(2048);

        builder.Property(x => x.Project)
            .IsRequired()
            .HasMaxLength(256);

        builder.Property(x => x.CompletedStatesJson)
            .IsRequired();

        builder.Property(x => x.ActiveState)
            .HasMaxLength(256);

        builder.Property(x => x.CompletedState)
            .HasMaxLength(256);

        builder.Property(x => x.PatEnvironmentVariable)
            .IsRequired()
            .HasMaxLength(256);

        builder.Property(x => x.CreatedAt)
            .IsRequired();

        builder.Property(x => x.UpdatedAt)
            .IsRequired();
    }
}

using AgentController.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AgentController.Infrastructure.Data.Configurations;

/// <summary>
/// EF Core configuration for managed Azure DevOps environment profiles.
/// </summary>
internal sealed class AzureDevOpsEnvironmentEntityConfiguration
    : IEntityTypeConfiguration<AzureDevOpsEnvironmentEntity>
{
    public void Configure(EntityTypeBuilder<AzureDevOpsEnvironmentEntity> builder)
    {
        builder.ToTable("AzureDevOpsEnvironments");

        builder.HasKey(x => x.Key);

        builder.Property(x => x.Key)
            .HasMaxLength(128);

        builder.Property(x => x.DisplayName)
            .IsRequired()
            .HasMaxLength(256);

        builder.Property(x => x.Enabled)
            .IsRequired();

        builder.Property(x => x.OrganizationUrl)
            .IsRequired()
            .HasMaxLength(2048);

        builder.Property(x => x.Project)
            .IsRequired()
            .HasMaxLength(256);

        builder.Property(x => x.WorkItemType)
            .IsRequired()
            .HasMaxLength(128);

        builder.Property(x => x.EligibleTagsJson)
            .IsRequired();

        builder.Property(x => x.ExcludedTagsJson)
            .IsRequired();

        builder.Property(x => x.EligibleStatesJson)
            .IsRequired();

        builder.Property(x => x.ExcludedStatesJson)
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

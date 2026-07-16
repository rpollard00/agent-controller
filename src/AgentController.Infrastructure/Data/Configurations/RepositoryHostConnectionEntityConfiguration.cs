using AgentController.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AgentController.Infrastructure.Data.Configurations;

/// <summary>
/// EF Core configuration for managed repository host connection profiles.
/// </summary>
internal sealed class RepositoryHostConnectionEntityConfiguration
    : IEntityTypeConfiguration<RepositoryHostConnectionEntity>
{
    public void Configure(EntityTypeBuilder<RepositoryHostConnectionEntity> builder)
    {
        builder.ToTable("RepositoryHostConnections");

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

        builder.Property(x => x.OrganizationUrl)
            .IsRequired()
            .HasMaxLength(2048);

        builder.Property(x => x.Project)
            .IsRequired()
            .HasMaxLength(256);

        builder.Property(x => x.PersonalAccessTokenReferenceKind)
            .IsRequired()
            .HasMaxLength(64);

        builder.Property(x => x.PersonalAccessTokenReferenceId)
            .IsRequired()
            .HasMaxLength(256);

        builder.Property(x => x.CreatedAt)
            .IsRequired();

        builder.Property(x => x.UpdatedAt)
            .IsRequired();
    }
}

using AgentController.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AgentController.Infrastructure.Data.Configurations;

/// <summary>
/// EF Core configuration for managed repository host connection profiles.
/// Uses <see cref="ConnectionEntityConfigurationHelper"/> for shared connection fields.
/// </summary>
internal sealed class RepositoryHostConnectionEntityConfiguration
    : IEntityTypeConfiguration<RepositoryHostConnectionEntity>
{
    public void Configure(EntityTypeBuilder<RepositoryHostConnectionEntity> builder)
    {
        builder.ToTable("RepositoryHostConnections");

        builder.HasKey(x => x.Key);

        // Apply common connection entity configurations (Key, DisplayName, Enabled,
        // Provider, OrganizationUrl, Project, CreatedAt, UpdatedAt).
        ConnectionEntityConfigurationHelper.ApplyCommonConfigurations(builder);

        // Repository-host-specific fields.
        builder.Property(x => x.PersonalAccessTokenSecretName)
            .IsRequired()
            .HasMaxLength(256);
    }
}

using AgentController.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AgentController.Infrastructure.Data.Configurations;

/// <summary>
/// EF Core mapping for <see cref="ConnectionEntity"/>.
/// </summary>
internal sealed class ConnectionEntityConfiguration : IEntityTypeConfiguration<ConnectionEntity>
{
    public void Configure(EntityTypeBuilder<ConnectionEntity> builder)
    {
        builder.ToTable("Connections");
        builder.HasKey(e => e.Key);

        builder.Property(e => e.Key)
            .IsRequired()
            .HasMaxLength(128);

        builder.Property(e => e.DisplayName)
            .IsRequired()
            .HasMaxLength(256);

        builder.Property(e => e.Enabled).IsRequired();

        builder.Property(e => e.Provider)
            .IsRequired()
            .HasMaxLength(128);

        builder.Property(e => e.Capabilities).IsRequired();

        builder.Property(e => e.ProviderSettingsJson)
            .HasMaxLength(65536)
            .IsRequired(false);

        builder.Property(e => e.CreatedAt).IsRequired();

        builder.Property(e => e.UpdatedAt).IsRequired();
    }
}

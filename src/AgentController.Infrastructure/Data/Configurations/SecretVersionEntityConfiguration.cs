using AgentController.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AgentController.Infrastructure.Data.Configurations;

/// <summary>
/// EF Core configuration for the SecretVersions table.
/// </summary>
internal sealed class SecretVersionEntityConfiguration
    : IEntityTypeConfiguration<SecretVersionEntity>
{
    public void Configure(EntityTypeBuilder<SecretVersionEntity> builder)
    {
        builder.ToTable("SecretVersions");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id)
            .IsRequired()
            .HasMaxLength(256);

        builder.Property(x => x.NamedSecretId)
            .IsRequired()
            .HasMaxLength(256);

        builder.HasIndex(x => new { x.NamedSecretId, x.VersionNumber })
            .IsUnique();

        builder.Property(x => x.VersionNumber)
            .IsRequired();

        builder.Property(x => x.EncryptedValue)
            .IsRequired();

        builder.Property(x => x.Nonce)
            .IsRequired();

        builder.Property(x => x.WrappedDek)
            .IsRequired();

        builder.Property(x => x.CreatedAt)
            .IsRequired();
    }
}

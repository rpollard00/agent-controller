using AgentController.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AgentController.Infrastructure.Data.Configurations;

/// <summary>
/// EF Core configuration for the NamedSecrets table.
/// </summary>
internal sealed class NamedSecretEntityConfiguration
    : IEntityTypeConfiguration<NamedSecretEntity>
{
    public void Configure(EntityTypeBuilder<NamedSecretEntity> builder)
    {
        builder.ToTable("NamedSecrets");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id)
            .IsRequired()
            .HasMaxLength(256);

        builder.Property(x => x.Name)
            .IsRequired()
            .HasMaxLength(256);

        builder.HasIndex(x => x.Name)
            .IsUnique();

        builder.Property(x => x.SecretType)
            .IsRequired()
            .HasMaxLength(64);

        builder.HasIndex(x => x.SecretType);

        builder.Property(x => x.CreatedAt)
            .IsRequired();

        builder.HasMany(x => x.Versions)
            .WithOne(x => x.NamedSecret)
            .HasForeignKey(x => x.NamedSecretId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

using AgentController.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AgentController.Infrastructure.Data.Configurations;

/// <summary>
/// EF Core configuration for the Secrets table.
/// </summary>
internal sealed class SecretEntityConfiguration
    : IEntityTypeConfiguration<SecretEntity>
{
    public void Configure(EntityTypeBuilder<SecretEntity> builder)
    {
        builder.ToTable("Secrets");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id)
            .IsRequired()
            .HasMaxLength(256);

        builder.Property(x => x.Value)
            .IsRequired()
            .HasMaxLength(4096);

        builder.Property(x => x.Label)
            .HasMaxLength(256);

        builder.Property(x => x.CreatedAt)
            .IsRequired();

        builder.Property(x => x.UpdatedAt)
            .IsRequired();
    }
}

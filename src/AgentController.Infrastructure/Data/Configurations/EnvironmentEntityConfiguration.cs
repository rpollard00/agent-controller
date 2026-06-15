using AgentController.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AgentController.Infrastructure.Data.Configurations;

/// <summary>
/// EF Core entity type configuration for <see cref="EnvironmentEntity"/>.
/// </summary>
internal sealed class EnvironmentEntityConfiguration : IEntityTypeConfiguration<EnvironmentEntity>
{
    public void Configure(EntityTypeBuilder<EnvironmentEntity> builder)
    {
        builder.ToTable("Environments");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id)
            .HasMaxLength(128);

        builder.Property(x => x.ProviderType)
            .IsRequired()
            .HasMaxLength(128);

        builder.Property(x => x.RunId)
            .IsRequired()
            .HasMaxLength(128);

        builder.Property(x => x.RootPath)
            .IsRequired()
            .HasMaxLength(1024);

        builder.Property(x => x.Status)
            .IsRequired()
            .HasMaxLength(128);

        // JSON-like column: stored as TEXT
        builder.Property(x => x.MetadataJson)
            .HasColumnName("MetadataJson");

        // Timestamps
        builder.Property(x => x.CreatedAt)
            .IsRequired();

        builder.Property(x => x.UpdatedAt)
            .IsRequired();

        // Index for run-scoped environment lookup
        builder.HasIndex(x => x.RunId)
            .HasDatabaseName("IX_Environments_RunId");

        builder.HasIndex(x => x.Status)
            .HasDatabaseName("IX_Environments_Status");
    }
}

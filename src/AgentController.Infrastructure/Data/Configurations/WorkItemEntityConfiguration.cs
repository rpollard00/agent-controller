using AgentController.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AgentController.Infrastructure.Data.Configurations;

/// <summary>
/// EF Core entity type configuration for <see cref="WorkItemEntity"/>.
/// </summary>
internal sealed class WorkItemEntityConfiguration : IEntityTypeConfiguration<WorkItemEntity>
{
    public void Configure(EntityTypeBuilder<WorkItemEntity> builder)
    {
        builder.ToTable("WorkItems");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id)
            .HasMaxLength(128);

        builder.Property(x => x.ExternalSource)
            .IsRequired()
            .HasMaxLength(128);

        builder.Property(x => x.ExternalId)
            .IsRequired()
            .HasMaxLength(256);

        builder.Property(x => x.ExternalUrl)
            .HasMaxLength(2048);

        builder.Property(x => x.RepoKey)
            .IsRequired()
            .HasMaxLength(256);

        builder.Property(x => x.Title)
            .IsRequired()
            .HasMaxLength(1024);

        builder.Property(x => x.Body);

        // JSON-like columns: stored as TEXT
        builder.Property(x => x.AcceptanceCriteriaJson)
            .HasColumnName("AcceptanceCriteriaJson");

        builder.Property(x => x.Priority);

        builder.Property(x => x.Status)
            .HasMaxLength(128);

        // JSON-like column: stored as TEXT
        builder.Property(x => x.TagsJson)
            .HasColumnName("TagsJson");

        builder.Property(x => x.AssignedTo)
            .HasMaxLength(256);

        builder.Property(x => x.Source)
            .IsRequired()
            .HasMaxLength(128);

        builder.Property(x => x.LeaseOwner)
            .HasMaxLength(256);

        // Timestamps
        builder.Property(x => x.CreatedAt)
            .IsRequired();

        builder.Property(x => x.UpdatedAt)
            .IsRequired();

        // Indexes for polling: efficient lookup by status and lease expiration
        builder.HasIndex(x => new { x.Status, x.LeaseExpiresAt })
            .HasDatabaseName("IX_WorkItems_Status_LeaseExpiresAt");

        // Index for repository-scoped queries
        builder.HasIndex(x => x.RepoKey)
            .HasDatabaseName("IX_WorkItems_RepoKey");

        // Index for work source queries
        builder.HasIndex(x => x.Source)
            .HasDatabaseName("IX_WorkItems_Source");
    }
}

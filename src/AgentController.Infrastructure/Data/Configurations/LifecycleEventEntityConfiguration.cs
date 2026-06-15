using AgentController.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AgentController.Infrastructure.Data.Configurations;

/// <summary>
/// EF Core entity type configuration for <see cref="LifecycleEventEntity"/>.
/// Includes a unique filtered index on (RunId, EventId) WHERE EventId IS NOT NULL
/// for runtime event idempotency.
/// </summary>
internal sealed class LifecycleEventEntityConfiguration : IEntityTypeConfiguration<LifecycleEventEntity>
{
    public void Configure(EntityTypeBuilder<LifecycleEventEntity> builder)
    {
        builder.ToTable("LifecycleEvents");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id)
            .HasMaxLength(128);

        builder.Property(x => x.RunId)
            .IsRequired()
            .HasMaxLength(128);

        builder.Property(x => x.EventId)
            .HasMaxLength(256);

        builder.Property(x => x.EventType)
            .IsRequired()
            .HasMaxLength(128);

        builder.Property(x => x.Severity)
            .IsRequired();

        builder.Property(x => x.Message);

        // JSON-like column: stored as TEXT
        builder.Property(x => x.PayloadJson)
            .HasColumnName("PayloadJson");

        builder.Property(x => x.CreatedAt)
            .IsRequired();

        builder.Property(x => x.UpdatedAt)
            .IsRequired();

        // Unique filtered index for runtime event idempotency:
        // only enforces uniqueness when EventId is present (non-null).
        // Controller-internal events have null EventId and are not constrained.
        builder.HasIndex(x => new { x.RunId, x.EventId })
            .IsUnique()
            .HasFilter("[EventId] IS NOT NULL")
            .HasDatabaseName("IX_LifecycleEvents_RunId_EventId_Unique");

        // Index for listing events by run, ordered by creation time
        builder.HasIndex(x => new { x.RunId, x.CreatedAt })
            .HasDatabaseName("IX_LifecycleEvents_RunId_CreatedAt");
    }
}

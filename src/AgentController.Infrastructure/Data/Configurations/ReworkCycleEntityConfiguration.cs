using AgentController.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AgentController.Infrastructure.Data.Configurations;

/// <summary>
/// EF Core entity type configuration for <see cref="ReworkCycleEntity"/>.
/// </summary>
internal sealed class ReworkCycleEntityConfiguration : IEntityTypeConfiguration<ReworkCycleEntity>
{
    public void Configure(EntityTypeBuilder<ReworkCycleEntity> builder)
    {
        builder.ToTable("ReworkCycles");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id)
            .HasMaxLength(128);

        builder.Property(x => x.WorkItemId)
            .IsRequired()
            .HasMaxLength(128);

        builder.Property(x => x.CycleNumber)
            .IsRequired();

        builder.Property(x => x.PriorRunId)
            .IsRequired()
            .HasMaxLength(128);

        builder.Property(x => x.BranchName)
            .IsRequired()
            .HasMaxLength(512);

        builder.Property(x => x.PullRequestUrl)
            .IsRequired()
            .HasMaxLength(2048);

        builder.Property(x => x.BaseCommitSha)
            .IsRequired()
            .HasMaxLength(40);

        builder.Property(x => x.FeedbackBundleJson);

        builder.Property(x => x.FeedbackBundleId)
            .IsRequired()
            .HasMaxLength(64);

        builder.Property(x => x.Status)
            .IsRequired();

        builder.Property(x => x.NewRunId)
            .HasMaxLength(128);

        // Timestamps
        builder.Property(x => x.CreatedAt)
            .IsRequired();

        builder.Property(x => x.ReactivatedAt);

        builder.Property(x => x.ConsumedAt);

        // Unique index on FeedbackBundleId — hard idempotency guard
        // against double-materialization of the same feedback bundle.
        builder.HasIndex(x => x.FeedbackBundleId)
            .IsUnique()
            .HasDatabaseName("IX_ReworkCycles_FeedbackBundleId");

        // Index for claim-time lookup: find pending cycles by work item.
        builder.HasIndex(x => new { x.WorkItemId, x.Status })
            .HasDatabaseName("IX_ReworkCycles_WorkItemId_Status");
    }
}

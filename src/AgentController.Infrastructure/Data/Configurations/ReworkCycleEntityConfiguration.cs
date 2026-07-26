using AgentController.Domain;
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

        builder.Property(x => x.RequestMode)
            .IsRequired()
            .HasDefaultValue((int)ReworkRequestMode.Revival);

        builder.Property(x => x.CanonicalPullRequestKey)
            .HasMaxLength(1024);

        builder.Property(x => x.PullRequestEnvironmentKey)
            .HasMaxLength(128);

        builder.Property(x => x.PullRequestRepositoryKey)
            .HasMaxLength(256);

        builder.Property(x => x.PullRequestId)
            .HasMaxLength(128);

        builder.Property(x => x.PullRequestTargetBranch)
            .HasMaxLength(512);

        builder.Property(x => x.WorkItemId)
            .IsRequired()
            .HasMaxLength(128);

        builder.Property(x => x.CycleNumber)
            .IsRequired();

        builder.Property(x => x.PriorRunId)
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

        builder.Property(x => x.CorrelationId)
            .HasMaxLength(128);

        builder.Property(x => x.Status)
            .IsRequired();

        builder.Property(x => x.NewRunId)
            .HasMaxLength(128);

        // Timestamps
        builder.Property(x => x.CreatedAt)
            .IsRequired();

        builder.Property(x => x.ReactivatedAt);

        builder.Property(x => x.ConsumedAt);

        // Canonical requests are deduplicated per workflow and PR. Rows created
        // before canonical identity was persisted retain the global bundle guard.
        builder.HasIndex(x => new
            {
                x.RequestMode,
                x.CanonicalPullRequestKey,
                x.FeedbackBundleId,
            })
            .IsUnique()
            .HasFilter("\"CanonicalPullRequestKey\" IS NOT NULL")
            .HasDatabaseName("IX_ReworkCycles_RequestMode_PrKey_FeedbackBundleId");

        builder.HasIndex(x => x.FeedbackBundleId)
            .IsUnique()
            .HasFilter("\"CanonicalPullRequestKey\" IS NULL")
            .HasDatabaseName("IX_ReworkCycles_FeedbackBundleId");

        builder.HasIndex(x => x.CorrelationId)
            .IsUnique()
            .HasFilter("\"CorrelationId\" IS NOT NULL")
            .HasDatabaseName("IX_ReworkCycles_CorrelationId");

        builder.HasIndex(x => new
            {
                x.RequestMode,
                x.CanonicalPullRequestKey,
                x.CycleNumber,
            })
            .IsUnique()
            .HasFilter("\"CanonicalPullRequestKey\" IS NOT NULL")
            .HasDatabaseName("IX_ReworkCycles_RequestMode_PrKey_CycleNumber");

        builder.HasIndex(x => new { x.RequestMode, x.CanonicalPullRequestKey, x.Status })
            .HasDatabaseName("IX_ReworkCycles_RequestMode_PrKey_Status");

        // Index for claim-time lookup: find pending cycles by work item.
        builder.HasIndex(x => new { x.WorkItemId, x.Status })
            .HasDatabaseName("IX_ReworkCycles_WorkItemId_Status");
    }
}

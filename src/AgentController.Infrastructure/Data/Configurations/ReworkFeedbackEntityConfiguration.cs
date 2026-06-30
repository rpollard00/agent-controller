using AgentController.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AgentController.Infrastructure.Data.Configurations;

/// <summary>
/// EF Core entity type configuration for <see cref="ReworkFeedbackEntity"/>.
/// </summary>
internal sealed class ReworkFeedbackEntityConfiguration : IEntityTypeConfiguration<ReworkFeedbackEntity>
{
    public void Configure(EntityTypeBuilder<ReworkFeedbackEntity> builder)
    {
        builder.ToTable("ReworkFeedback");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id)
            .HasMaxLength(128);

        builder.Property(x => x.OriginatingRunId)
            .IsRequired()
            .HasMaxLength(128);

        builder.Property(x => x.PullRequestId)
            .IsRequired()
            .HasMaxLength(128);

        builder.Property(x => x.FeedbackBundleId)
            .IsRequired()
            .HasMaxLength(64);

        builder.Property(x => x.FirstQualifyingCommentAt)
            .IsRequired();

        builder.Property(x => x.LastQualifyingCommentAt)
            .IsRequired();

        builder.Property(x => x.ThreadCount)
            .IsRequired();

        builder.Property(x => x.Status)
            .IsRequired();

        // Timestamps
        builder.Property(x => x.CreatedAt)
            .IsRequired();

        builder.Property(x => x.UpdatedAt)
            .IsRequired();

        // Unique index on (PullRequestId, FeedbackBundleId) — prevents
        // duplicate soak rows for the same bundle on the same PR.
        builder.HasIndex(x => new { x.PullRequestId, x.FeedbackBundleId })
            .IsUnique()
            .HasDatabaseName("IX_ReworkFeedback_PullRequestId_FeedbackBundleId");

        // Index for listing Watching rows (soak window scan).
        builder.HasIndex(x => x.Status)
            .HasDatabaseName("IX_ReworkFeedback_Status");
    }
}

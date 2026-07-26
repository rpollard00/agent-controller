using AgentController.Domain;
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

        builder.Property(x => x.RequestMode)
            .IsRequired()
            .HasDefaultValue((int)ReworkRequestMode.Revival);

        builder.Property(x => x.CanonicalPullRequestKey)
            .HasMaxLength(1024);

        builder.Property(x => x.PullRequestEnvironmentKey)
            .HasMaxLength(128);

        builder.Property(x => x.PullRequestRepositoryKey)
            .HasMaxLength(256);

        builder.Property(x => x.OriginatingRunId)
            .HasMaxLength(128);

        builder.Property(x => x.PullRequestId)
            .IsRequired()
            .HasMaxLength(128);

        builder.Property(x => x.PullRequestUrl)
            .HasMaxLength(2048);

        builder.Property(x => x.PullRequestSourceBranch)
            .HasMaxLength(512);

        builder.Property(x => x.PullRequestTargetBranch)
            .HasMaxLength(512);

        builder.Property(x => x.PullRequestSourceCommitSha)
            .HasMaxLength(128);

        builder.Property(x => x.FeedbackBundleId)
            .IsRequired()
            .HasMaxLength(64);

        builder.Property(x => x.FeedbackBundleJson)
            .IsRequired();

        builder.Property(x => x.FirstQualifyingCommentAt)
            .IsRequired();

        builder.Property(x => x.LastQualifyingCommentAt)
            .IsRequired();

        builder.Property(x => x.ThreadCount)
            .IsRequired();

        builder.Property(x => x.CorrelationId)
            .HasMaxLength(128);

        builder.Property(x => x.AssistanceStoryWorkItemId)
            .HasMaxLength(128);

        builder.Property(x => x.AssistanceStoryExternalId)
            .HasMaxLength(128);

        builder.Property(x => x.AssistanceStoryUrl)
            .HasMaxLength(2048);

        builder.Property(x => x.Status)
            .IsRequired();

        // Timestamps
        builder.Property(x => x.CreatedAt)
            .IsRequired();

        builder.Property(x => x.UpdatedAt)
            .IsRequired();

        // Canonically identified rows are isolated by workflow and PR. Legacy
        // Revival rows retain their provider-ID uniqueness fallback.
        builder.HasIndex(x => new
            {
                x.RequestMode,
                x.CanonicalPullRequestKey,
                x.FeedbackBundleId,
            })
            .IsUnique()
            .HasFilter("\"CanonicalPullRequestKey\" IS NOT NULL")
            .HasDatabaseName("IX_ReworkFeedback_RequestMode_PrKey_FeedbackBundleId");

        builder.HasIndex(x => new { x.RequestMode, x.PullRequestId, x.FeedbackBundleId })
            .IsUnique()
            .HasFilter("\"CanonicalPullRequestKey\" IS NULL")
            .HasDatabaseName("IX_ReworkFeedback_LegacyPullRequest_FeedbackBundleId");

        builder.HasIndex(x => x.CorrelationId)
            .IsUnique()
            .HasFilter("\"CorrelationId\" IS NOT NULL")
            .HasDatabaseName("IX_ReworkFeedback_CorrelationId");

        builder.HasIndex(x => new { x.RequestMode, x.CanonicalPullRequestKey, x.Status })
            .HasDatabaseName("IX_ReworkFeedback_RequestMode_PrKey_Status");

        // Index for listing Watching rows (soak window scan).
        builder.HasIndex(x => x.Status)
            .HasDatabaseName("IX_ReworkFeedback_Status");
    }
}

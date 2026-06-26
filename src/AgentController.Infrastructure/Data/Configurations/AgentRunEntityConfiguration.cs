using AgentController.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AgentController.Infrastructure.Data.Configurations;

/// <summary>
/// EF Core entity type configuration for <see cref="AgentRunEntity"/>.
/// </summary>
internal sealed class AgentRunEntityConfiguration : IEntityTypeConfiguration<AgentRunEntity>
{
    public void Configure(EntityTypeBuilder<AgentRunEntity> builder)
    {
        builder.ToTable("AgentRuns");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id)
            .HasMaxLength(128);

        builder.Property(x => x.WorkItemId)
            .HasMaxLength(128);

        builder.Property(x => x.RunAttempt)
            .IsRequired()
            .HasDefaultValue(1);

        builder.Property(x => x.PreviousRunId)
            .HasMaxLength(128);

        builder.Property(x => x.WorkerId)
            .HasMaxLength(256);

        builder.Property(x => x.RuntimeType)
            .HasMaxLength(128);

        builder.Property(x => x.RuntimeRunId)
            .HasMaxLength(256);

        builder.Property(x => x.EnvironmentId)
            .HasMaxLength(128);

        builder.Property(x => x.Status)
            .IsRequired();

        builder.Property(x => x.BranchName)
            .HasMaxLength(512);

        builder.Property(x => x.PullRequestUrl)
            .HasMaxLength(2048);

        builder.Property(x => x.ResultSummary);

        builder.Property(x => x.Error);

        // Timestamps
        builder.Property(x => x.CreatedAt)
            .IsRequired();

        builder.Property(x => x.UpdatedAt)
            .IsRequired();

        // Indexes for run lookup and stale-run detection
        builder.HasIndex(x => x.WorkItemId)
            .HasDatabaseName("IX_AgentRuns_WorkItemId");

        builder.HasIndex(x => x.EnvironmentId)
            .HasDatabaseName("IX_AgentRuns_EnvironmentId");

        // Compound index for stale-run detection: find runs by status and heartbeat
        builder.HasIndex(x => new { x.Status, x.LastHeartbeatAt })
            .HasDatabaseName("IX_AgentRuns_Status_LastHeartbeatAt");

        // Index for listing runs by status
        builder.HasIndex(x => x.Status)
            .HasDatabaseName("IX_AgentRuns_Status");
    }
}

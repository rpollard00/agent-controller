using AgentController.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AgentController.Infrastructure.Data.Configurations;

/// <summary>EF Core configuration for managed runtime environment profiles.</summary>
internal sealed class RuntimeEnvironmentEntityConfiguration
    : IEntityTypeConfiguration<RuntimeEnvironmentEntity>
{
    public void Configure(EntityTypeBuilder<RuntimeEnvironmentEntity> builder)
    {
        builder.ToTable("RuntimeEnvironments");

        builder.HasKey(x => x.Key);

        builder.Property(x => x.Key).HasMaxLength(128);

        builder.Property(x => x.DisplayName).IsRequired().HasMaxLength(256);

        builder.Property(x => x.Enabled).IsRequired();

        builder.Property(x => x.EnvironmentProvider).IsRequired().HasMaxLength(128);

        builder.Property(x => x.WorkspaceRoot).HasMaxLength(2048);

        builder.Property(x => x.RuntimeProvider).IsRequired().HasMaxLength(128);

        builder.Property(x => x.PiExecutablePath).HasMaxLength(2048);

        builder.Property(x => x.ControllerBaseUrl).HasMaxLength(2048);

        builder.Property(x => x.PtyWrapperPath).HasMaxLength(2048);

        builder.Property(x => x.PtyWrapperArgs).HasMaxLength(4096);

        builder.Property(x => x.LoadoutsJson).IsRequired();

        builder.Property(x => x.ForwardEnvironmentVariablesJson).IsRequired();

        builder.Property(x => x.CreatedAt).IsRequired();

        builder.Property(x => x.UpdatedAt).IsRequired();
    }
}

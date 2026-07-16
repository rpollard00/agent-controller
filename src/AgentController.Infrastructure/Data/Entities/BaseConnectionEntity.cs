using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AgentController.Infrastructure.Data.Entities;

/// <summary>
/// Shared base for managed connection entities (work-source environments,
/// repository host connections). Provides common fields: Key, DisplayName,
/// Enabled, Provider, OrganizationUrl, Project, CreatedAt, UpdatedAt.
/// </summary>
internal abstract class BaseConnectionEntity
{
    public string Key { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    public string Provider { get; set; } = string.Empty;

    public string OrganizationUrl { get; set; } = string.Empty;

    public string Project { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// Shared EF Core configuration helpers for <see cref="BaseConnectionEntity"/>-derived types.
/// </summary>
internal static class ConnectionEntityConfigurationHelper
{
    /// <summary>
    /// Apply common property configurations for a connection entity type.
    /// Callers should invoke this from their <c>Configure</c> method after
    /// setting the table name and key.
    /// </summary>
    public static void ApplyCommonConfigurations<TEntity>(
        EntityTypeBuilder<TEntity> builder
    ) where TEntity : BaseConnectionEntity
    {
        builder.Property(x => x.Key).HasMaxLength(128);

        builder.Property(x => x.DisplayName)
            .IsRequired()
            .HasMaxLength(256);

        builder.Property(x => x.Enabled).IsRequired();

        builder.Property(x => x.Provider)
            .IsRequired()
            .HasMaxLength(128);

        builder.Property(x => x.OrganizationUrl)
            .IsRequired()
            .HasMaxLength(2048);

        builder.Property(x => x.Project)
            .IsRequired()
            .HasMaxLength(256);

        builder.Property(x => x.CreatedAt).IsRequired();

        builder.Property(x => x.UpdatedAt).IsRequired();
    }
}

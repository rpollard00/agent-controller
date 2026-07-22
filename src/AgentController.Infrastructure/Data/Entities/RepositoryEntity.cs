using AgentController.Domain;

namespace AgentController.Infrastructure.Data.Entities;

/// <summary>
/// EF Core entity for the Repositories table.
/// Maps to the prototype data model defined in the architecture (§7.5).
/// </summary>
internal sealed class RepositoryEntity
{
    /// <summary>Unique key for this repository profile (PK).</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Remote URL to clone.</summary>
    public string CloneUrl { get; set; } = string.Empty;

    /// <summary>Default branch to check out after cloning.</summary>
    public string DefaultBranch { get; set; } = "main";

    /// <summary>Clone transport type (SSH, HTTPS+PAT, Local, or inferred).</summary>
    public CloneTransport Transport { get; set; } = CloneTransport.Unspecified;

    /// <summary>Name of the environment profile to use for this repository.</summary>
    public string EnvironmentProfile { get; set; } = string.Empty;

    /// <summary>Name of the runtime profile to use for this repository.</summary>
    public string RuntimeProfile { get; set; } = string.Empty;

    /// <summary>Optional key of the managed repository host connection profile.</summary>
    public string? RepositoryHostConnectionKey { get; set; }

    /// <summary>Provider-specific project name scoped to this repository.</summary>
    public string? Project { get; set; }

    /// <summary>Optional provider-specific remote identity (e.g. ADO repo GUID).</summary>
    public string? RemoteIdentity { get; set; }

    /// <summary>Optional key of the managed runtime environment profile.</summary>
    public string? RuntimeEnvironmentKey { get; set; }

    /// <summary>Optional name of the SSH-key secret used by this repository.</summary>
    public string? SshKeySecretName { get; set; }

    /// <summary>Optional pinned version of the SSH-key secret.</summary>
    public int? SshKeySecretVersion { get; set; }

    /// <summary>
    /// When <c>true</c>, the SSH key is provided by the runner environment
    /// rather than by a managed secret reference.
    /// </summary>
    public bool SshKeyInheritEnvironment { get; set; }

    /// <summary>When the record was first persisted.</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>When the record was last mutated.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

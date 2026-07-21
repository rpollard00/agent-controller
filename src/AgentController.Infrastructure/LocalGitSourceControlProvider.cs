using System.Diagnostics;
using AgentController.Application;
using AgentController.Domain;
using AgentController.Infrastructure.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentController.Infrastructure;

/// <summary>
/// Local-git implementation of <see cref="ISourceControlProvider"/>.
///
/// Uses <c>git clone</c> for all clone URL types:
/// <list type="bullet">
///   <item>Local paths: <c>/home/user/projects/repo</c> or <c>~/projects/repo</c></item>
///   <item>File URLs: <c>file:///home/user/projects/repo</c></item>
///   <item>Remote URLs: <c>https://...</c>, <c>git@...</c>, <c>ssh://...</c></item>
/// </list>
///
/// Git natively handles local paths and <c>file://</c> URLs — for local paths
/// it creates hardlinked clones, and for <c>file://</c> URLs it follows the
/// standard clone protocol over the local filesystem.
///
/// Registered as a singleton so it is safe for consumption by singleton consumers
/// such as <see cref="BackgroundService"/>. HTTPS credentials are resolved per operation
/// through a short-lived service scope. Uses <see cref="IOptionsMonitor{TOptions}"/>
/// for live config reload support.
/// </summary>
public sealed partial class LocalGitSourceControlProvider : ISourceControlProvider
{
    private readonly IOptionsMonitor<AgentControllerOptions> _controllerOptions;
    private readonly ILogger<LocalGitSourceControlProvider> _logger;
    private readonly RepositoryCloneCredentialResolver? _credentialResolver;

    /// <summary>
    /// Maximum time a git clone operation is allowed to run before being terminated.
    /// </summary>
    private static readonly TimeSpan CloneTimeout = TimeSpan.FromMinutes(10);

    public LocalGitSourceControlProvider(
        IOptionsMonitor<AgentControllerOptions> controllerOptions,
        ILogger<LocalGitSourceControlProvider> logger,
        IServiceScopeFactory? scopeFactory = null
    )
        : this(
            controllerOptions,
            logger,
            scopeFactory is null ? null : new RepositoryCloneCredentialResolver(scopeFactory)
        )
    { }

    internal LocalGitSourceControlProvider(
        IOptionsMonitor<AgentControllerOptions> controllerOptions,
        ILogger<LocalGitSourceControlProvider> logger,
        RepositoryCloneCredentialResolver? credentialResolver
    )
    {
        _controllerOptions = controllerOptions;
        _logger = logger;
        _credentialResolver = credentialResolver;
    }

    /// <inheritdoc />
    public async Task<RepositoryCheckout> CloneAsync(
        RepositorySpec spec,
        EnvironmentHandle environment,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(spec.CloneUrl))
        {
            throw new InvalidOperationException(
                $"Cannot clone repository '{spec.RepoKey}': cloneUrl is empty. " +
                "Ensure the repository profile includes a cloneUrl.");
        }

        if (string.IsNullOrWhiteSpace(environment.RootPath))
        {
            throw new InvalidOperationException(
                $"Cannot clone repository '{spec.RepoKey}': environment has no root path. " +
                "Ensure the environment provider created the workspace first.");
        }

        var cloneUrl = RemoveCredentialsFromCloneUrl(NormalizeCloneUrl(spec.CloneUrl));
        var resolution = spec.Profile is null
            ? null
            : RepositoryCloneTransportResolver.Resolve(
                spec.Profile,
                spec.RepositoryConnection
            );
        var transport = resolution?.Transport ?? ResolveTransport(spec.Transport, cloneUrl);
        var targetDir = Path.Combine(environment.RootPath, "repo");

        // Ensure the parent directory exists (environment provider should have created it,
        // but be defensive).
        var parentDir = Path.GetDirectoryName(targetDir);
        if (parentDir is not null)
        {
            Directory.CreateDirectory(parentDir);
        }

        Log.CloningRepository(_logger, spec.RepoKey, cloneUrl, targetDir, transport);

        // Build git clone arguments.
        // --quiet suppresses progress output.
        // --branch checks out a specific branch after clone.
        var args = new List<string> { "clone", "--quiet" };

        using var httpsCredentials =
            transport == CloneTransport.HttpsPat
                ? await CreateHttpsCredentialsAsync(spec, resolution, cancellationToken)
                : null;

        if (httpsCredentials is not null)
        {
            // Reset inherited credential helpers so the operation cannot persist the PAT.
            // The ephemeral askpass helper is the sole credential source.
            args.Add("-c");
            args.Add("credential.helper=");
        }

        if (!string.IsNullOrWhiteSpace(spec.DefaultBranch))
        {
            args.Add("--branch");
            args.Add(spec.DefaultBranch);
        }

        args.Add(cloneUrl);
        args.Add(targetDir);

        var (exitCode, stdErr) = await RunGitAsync(
            args,
            httpsCredentials?.Environment,
            cancellationToken
        );

        if (exitCode != 0)
        {
            var errorDetail = stdErr.Length > 0
                ? $"\n{stdErr.Trim()}"
                : string.Empty;
            throw new InvalidOperationException(
                $"git clone failed for repository '{spec.RepoKey}' " +
                $"(cloneUrl: '{cloneUrl}', exit code: {exitCode}).{errorDetail}");
        }

        Log.CloneCompleted(_logger, spec.RepoKey, targetDir, transport);

        // Resolve the HEAD commit SHA for auditability.
        var commitSha = await GetHeadCommitShaAsync(targetDir, cancellationToken);

        return new RepositoryCheckout
        {
            RepoKey = spec.RepoKey,
            LocalPath = targetDir,
            Branch = spec.DefaultBranch,
            CommitSha = commitSha,
            Transport = transport,
            ClonedAt = DateTimeOffset.UtcNow,
        };
    }

    /// <inheritdoc />
    public async Task<SourceControlStatus> GetStatusAsync(
        SourceControlRef sourceControlRef,
        CancellationToken cancellationToken)
    {
        // For local-git provider, status inspection is limited to existence checks.
        // Branch and PR inspection against remote Azure DevOps Repos would require
        // ADO-specific API calls, which is out of scope for this provider.
        //
        // Future: if we have a local clone path from a previous run, we could
        // inspect the local clone. For now, return a basic existence check
        // based on whether we can infer a path.

        return await Task.FromResult(new SourceControlStatus
        {
            Exists = false,
            Branch = sourceControlRef.Branch,
            CommitSha = sourceControlRef.CommitSha,
            PullRequestUrl = null,
            PullRequestStatus = null,
        });
    }

    /// <inheritdoc />
    public async Task<ClonePreflightResult> CheckClonePreflightAsync(
        RepositorySpec spec,
        CancellationToken cancellationToken)
    {
        var cloneUrl = string.IsNullOrWhiteSpace(spec.CloneUrl)
            ? string.Empty
            : RemoveCredentialsFromCloneUrl(NormalizeCloneUrl(spec.CloneUrl));

        var transport = ResolveTransport(spec.Transport, cloneUrl);

        Log.PreflightStarting(_logger, spec.RepoKey, cloneUrl, transport);

        // ── 1. Validate clone URL is present ────────────────────────
        if (string.IsNullOrWhiteSpace(cloneUrl))
        {
            var reason = $"Clone URL is empty for repository '{spec.RepoKey}'.";
            Log.PreflightFailed(_logger, spec.RepoKey, transport, reason);
            return ClonePreflightResult.Failed(transport, cloneUrl, reason);
        }

        // ── 2. Validate transport prerequisites ────────────────────
        var prereqFailure = await CheckTransportPrerequisitesAsync(transport, cloneUrl, cancellationToken);
        if (prereqFailure is not null)
        {
            Log.PreflightFailed(_logger, spec.RepoKey, transport, prereqFailure);
            return ClonePreflightResult.Failed(transport, cloneUrl, prereqFailure);
        }

        // ── 3. For local paths, verify the directory exists ────────
        if (transport == CloneTransport.Local)
        {
            // After normalization, local paths are absolute or relative.
            // file:// URLs are handled by git ls-remote below.
            if (!cloneUrl.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                if (!Directory.Exists(cloneUrl))
                {
                    var reason = $"Local repository path does not exist: '{cloneUrl}'.";
                    Log.PreflightFailed(_logger, spec.RepoKey, transport, reason);
                    return ClonePreflightResult.Failed(transport, cloneUrl, reason);
                }
            }
        }

        // ── 4. Non-interactive git ls-remote probe ────────────────
        // Use a short timeout — this is a preflight, not a clone.
        var lsRemoteTimeout = TimeSpan.FromSeconds(30);
        var (exitCode, stdErr) = await RunGitLsRemoteAsync(
            cloneUrl,
            spec.DefaultBranch,
            lsRemoteTimeout,
            cancellationToken);

        if (exitCode != 0)
        {
            var errorDetail = stdErr.Length > 0
                ? $" git reported: {stdErr.Trim()}"
                : string.Empty;
            var reason = $"git ls-remote failed for '{cloneUrl}' (exit code: {exitCode}).{errorDetail}";
            Log.PreflightFailed(_logger, spec.RepoKey, transport, reason);
            return ClonePreflightResult.Failed(transport, cloneUrl, reason);
        }

        Log.PreflightPassed(_logger, spec.RepoKey, transport);
        return ClonePreflightResult.Ok(transport, cloneUrl);
    }

    /// <summary>
    /// Check that the transport prerequisites are available on this host.
    /// Returns null if all prerequisites are met, or a failure reason string.
    /// </summary>
    private static async Task<string?> CheckTransportPrerequisitesAsync(
        CloneTransport transport,
        string cloneUrl,
        CancellationToken cancellationToken)
    {
        return transport switch
        {
            CloneTransport.Ssh => await CheckSshPrerequisitesAsync(cloneUrl, cancellationToken),
            CloneTransport.HttpsPat => CheckHttpsPatPrerequisites(cloneUrl),
            CloneTransport.Local or CloneTransport.Unspecified => null,
            _ => null,
        };
    }

    /// <summary>
    /// Verify SSH prerequisites: ssh command available, SSH key exists.
    /// </summary>
    private static async Task<string?> CheckSshPrerequisitesAsync(
        string cloneUrl,
        CancellationToken cancellationToken)
    {
        // Verify ssh command is available.
        var sshCheck = await RunPreflightCommandAsync(
            "ssh",
            ["-V"],
            TimeSpan.FromSeconds(5),
            cancellationToken);

        // ssh -V writes to stderr and exits with 0, so we accept non-zero too
        // as long as the process started (some ssh versions exit 255 for -V).
        if (sshCheck.ExitCode < 0)
        {
            return "SSH command not found. Ensure 'ssh' is installed and on PATH.";
        }

        // Check for an SSH identity file.
        // Common locations: ~/.ssh/id_ed25519, ~/.ssh/id_rsa, ~/.ssh/id_ecdsa
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var sshDir = Path.Combine(home, ".ssh");
        var keyPatterns = new[] { "id_ed25519", "id_rsa", "id_ecdsa" };

        bool hasKey = false;
        foreach (var pattern in keyPatterns)
        {
            if (File.Exists(Path.Combine(sshDir, pattern)))
            {
                hasKey = true;
                break;
            }
        }

        if (!hasKey)
        {
            return $"No SSH key found in '{sshDir}'. " +
                $"Expected one of: {string.Join(", ", keyPatterns)}. " +
                "Generate a key with 'ssh-keygen' and add it to the remote repository.";
        }

        return null;
    }

    /// <summary>
    /// Verify HTTPS+PAT prerequisites: the URL must contain embedded credentials
    /// (user:pass@host pattern) or GIT_TERMINAL_PROMPT=0 will cause auth failure.
    /// We only check that the URL looks like it could have PAT embedded.
    /// </summary>
    private static string? CheckHttpsPatPrerequisites(string cloneUrl)
    {
        // For HTTPS+PAT, the PAT is typically embedded in the URL as:
        // https://user:pat@dev.azure.com/... or https://pat@dev.azure.com/...
        // We can't fully validate the PAT here, but we can warn if no credentials
        // appear to be embedded. The actual clone will fail with a clear error
        // if PAT is missing, so this is a soft check.
        //
        // For preflight purposes, we accept any HTTPS URL — the git ls-remote
        // probe below will catch auth failures.
        return null;
    }

    /// <summary>
    /// Run <c>git ls-remote</c> against the clone URL as a lightweight connectivity probe.
    /// Uses the same non-interactive environment hardening as clone operations.
    /// </summary>
    private static async Task<(int ExitCode, string StdErr)> RunGitLsRemoteAsync(
        string cloneUrl,
        string? defaultBranch,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        // git ls-remote [<repository> [<ref>...]]
        // URL must come before ref arguments.
        var args = new List<string> { "ls-remote", cloneUrl };

        // If a branch is specified, limit output to that ref for a lighter probe.
        if (!string.IsNullOrWhiteSpace(defaultBranch))
        {
            args.Add($"refs/heads/{defaultBranch}");
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = Path.GetTempPath(),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
            },
        };

        // Apply non-interactive environment variables.
        foreach (var (key, value) in GitNonInteractiveEnv)
        {
            process.StartInfo.EnvironmentVariables[key] = value;
        }

        foreach (var arg in args)
        {
            process.StartInfo.ArgumentList.Add(arg);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        try
        {
            process.Start();

            var stdOutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            var stdErrTask = process.StandardError.ReadToEndAsync(cts.Token);

            await process.WaitForExitAsync(cts.Token);

            var _ = await stdOutTask; // discard stdout
            var stdErr = await stdErrTask;

            return (process.ExitCode, stdErr ?? string.Empty);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            return (-1, $"git ls-remote timed out after {timeout.TotalSeconds:F0}s.");
        }
        catch (Exception ex)
        {
            return (-1, $"git ls-remote process error: {ex.Message}");
        }
    }

    /// <summary>
    /// Run a simple command for preflight prerequisite checks.
    /// </summary>
    private static async Task<(int ExitCode, string StdErr)> RunPreflightCommandAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                WorkingDirectory = Path.GetTempPath(),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
            },
        };

        foreach (var arg in arguments)
        {
            process.StartInfo.ArgumentList.Add(arg);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        try
        {
            process.Start();

            var stdOutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            var stdErrTask = process.StandardError.ReadToEndAsync(cts.Token);

            await process.WaitForExitAsync(cts.Token);

            var _ = await stdOutTask;
            var stdErr = await stdErrTask;

            return (process.ExitCode, stdErr ?? string.Empty);
        }
        catch (FileNotFoundException)
        {
            // Command not found.
            return (-1, $"'{fileName}' not found on PATH.");
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }

    private async Task<GitAskPassCredentials?> CreateHttpsCredentialsAsync(
        RepositorySpec spec,
        RepositoryCloneTransportResolution? resolution,
        CancellationToken cancellationToken
    )
    {
        // Direct RepositorySpec callers remain compatible with public, unauthenticated
        // HTTPS repositories. Managed execution always supplies Profile + connection.
        if (spec.Profile is null)
        {
            return null;
        }

        if (resolution is null || !resolution.IsReady)
        {
            var reasons = resolution is null
                ? "clone transport credentials could not be resolved."
                : string.Join(" ", resolution.BlockingIssues.Select(issue => issue.Message));
            throw new InvalidOperationException(
                $"Cannot clone repository '{spec.RepoKey}' over HTTPS: {reasons}"
            );
        }

        if (
            resolution.CredentialSource
                != RepositoryCloneCredentialSource.ConnectionPersonalAccessToken
            || resolution.CredentialReference is null
        )
        {
            throw new InvalidOperationException(
                $"Cannot clone repository '{spec.RepoKey}' over HTTPS: "
                    + "the repository connection does not provide a PAT secret reference."
            );
        }

        if (_credentialResolver is null)
        {
            throw new InvalidOperationException(
                $"Cannot clone repository '{spec.RepoKey}' over HTTPS: "
                    + "clone credential resolution is not configured."
            );
        }

        var personalAccessToken = await _credentialResolver.ResolvePersonalAccessTokenAsync(
            resolution.CredentialReference,
            cancellationToken
        );
        return GitAskPassCredentials.Create(personalAccessToken);
    }

    /// <summary>
    /// Normalize a clone URL for use with <c>git clone</c>.
    ///
    /// Handles:
    /// <list type="bullet">
    ///   <item>Tilde expansion: <c>~/projects/repo</c> → <c>/home/user/projects/repo</c></item>
    ///   <item><c>file://</c> URLs: passed through as-is (git handles them natively)</item>
    ///   <item>Remote URLs: passed through as-is</item>
    /// </list>
    /// </summary>
    internal static string NormalizeCloneUrl(string rawUrl)
    {
        var url = rawUrl.Trim();

        // file:// URLs and remote URLs (https://, git@, ssh://) are passed through.
        if (url.StartsWith("file://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("git@", StringComparison.Ordinal) ||
            url.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }

        // Tilde expansion for bare local paths.
        if (url.StartsWith('~'))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (url.Length == 1)
            {
                return home;
            }

            // Handle ~/path and ~user/path (only ~/ is common; ~user is rare but supported).
            if (url[1] == '/' || url[1] == '\\')
            {
                return Path.Combine(home, url[2..]);
            }
        }

        return url;
    }

    /// <summary>
    /// Resolve the effective transport for a clone operation.
    /// If the spec provides an explicit transport, use it.
    /// Otherwise, infer from the clone URL pattern.
    /// </summary>
    internal static CloneTransport ResolveTransport(CloneTransport explicitTransport, string cloneUrl) =>
        RepositoryCloneTransportResolver.ResolveTransport(explicitTransport, cloneUrl);

    /// <summary>
    /// Removes URL user-info before execution and logging. Managed HTTPS credentials
    /// are supplied through askpass, so retaining embedded credentials would leak them
    /// into logs and the cloned repository's persisted <c>origin</c> remote.
    /// </summary>
    internal static string RemoveCredentialsFromCloneUrl(string cloneUrl)
    {
        if (
            !Uri.TryCreate(cloneUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrEmpty(uri.UserInfo)
        )
        {
            return cloneUrl;
        }

        var sanitized = new UriBuilder(uri)
        {
            UserName = string.Empty,
            Password = string.Empty,
        };
        return sanitized.Uri.AbsoluteUri;
    }

    /// <summary>
    /// Environment variables applied to every git subprocess to guarantee
    /// non-interactive execution. Prevents the worker from hanging on
    /// host-key or credential prompts.
    /// </summary>
    internal static readonly Dictionary<string, string?> GitNonInteractiveEnv =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // Prevent git from opening a terminal for credentials.
            ["GIT_TERMINAL_PROMPT"] = "0",
            // Force SSH into batch mode (never prompt) and accept new host keys
            // from the system known_hosts or accept them on first connect.
            ["GIT_SSH_COMMAND"] = "ssh -o BatchMode=yes -o StrictHostKeyChecking=accept-new",
        };

    /// <summary>
    /// Run a git command with the given arguments.
    /// Returns the process exit code and captured stderr.
    ///
    /// All git invocations are hardened to never block on interactive prompts:
    /// - GIT_TERMINAL_PROMPT=0 disables credential prompts
    /// - GIT_SSH_COMMAND enforces SSH BatchMode and StrictHostKeyChecking
    /// - stdin is disconnected (/dev/null) so no TTY can be inherited
    /// </summary>
    private static async Task<(int ExitCode, string StdErr)> RunGitAsync(
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?>? additionalEnvironment,
        CancellationToken cancellationToken
    )
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                // Use temp directory as working directory so git operations
                // never depend on the process-wide current directory.
                WorkingDirectory = Path.GetTempPath(),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                // Redirect stdin so the process never inherits the parent TTY.
                // Combined with GIT_TERMINAL_PROMPT=0 and SSH BatchMode this
                // guarantees no interactive prompt can ever be shown.
                RedirectStandardInput = true,
            },
        };

        // Apply non-interactive environment variables to every git invocation.
        foreach (var (key, value) in GitNonInteractiveEnv)
        {
            process.StartInfo.EnvironmentVariables[key] = value;
        }

        if (additionalEnvironment is not null)
        {
            foreach (var (key, value) in additionalEnvironment)
            {
                process.StartInfo.EnvironmentVariables[key] = value;
            }
        }

        foreach (var arg in arguments)
        {
            process.StartInfo.ArgumentList.Add(arg);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(CloneTimeout);

        try
        {
            process.Start();

            // Read stdout and stderr asynchronously to avoid deadlocks.
            var stdOutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            var stdErrTask = process.StandardError.ReadToEndAsync(cts.Token);

            await process.WaitForExitAsync(cts.Token);

            var stdErr = await stdErrTask;

            return (process.ExitCode, stdErr ?? string.Empty);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // Clone timed out — kill the process.
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw new InvalidOperationException(
                $"git clone timed out after {CloneTimeout.TotalMinutes:F0} minutes.");
        }
    }

    /// <summary>
    /// Get the HEAD commit SHA from a local git repository.
    /// </summary>
    private static async Task<string?> GetHeadCommitShaAsync(
        string repoPath,
        CancellationToken cancellationToken)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = "rev-parse HEAD",
                    WorkingDirectory = repoPath,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    // Redirect stdin so the process never inherits the parent TTY.
                    RedirectStandardInput = true,
                },
            };

            // Apply non-interactive environment variables.
            foreach (var (key, value) in GitNonInteractiveEnv)
            {
                process.StartInfo.EnvironmentVariables[key] = value;
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(30));

            process.Start();

            var output = await process.StandardOutput.ReadToEndAsync(cts.Token);
            await process.WaitForExitAsync(cts.Token);

            var sha = output.Trim();
            return sha.Length > 0 ? sha : null;
        }
        catch
        {
            // Best-effort: commit SHA is optional for checkout metadata.
            return null;
        }
    }

    private static partial class Log
    {
        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Cloning repository '{RepoKey}' from '{CloneUrl}' into '{TargetDir}' (transport: {Transport}).")]
        public static partial void CloningRepository(
            ILogger logger, string repoKey, string cloneUrl, string targetDir, CloneTransport transport);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Repository '{RepoKey}' cloned successfully into '{TargetDir}' (transport: {Transport}).")]
        public static partial void CloneCompleted(
            ILogger logger, string repoKey, string targetDir, CloneTransport transport);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Clone preflight starting for '{RepoKey}' (transport: {Transport}, url: {CloneUrl}).")]
        public static partial void PreflightStarting(
            ILogger logger, string repoKey, string cloneUrl, CloneTransport transport);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Clone preflight passed for '{RepoKey}' (transport: {Transport}).")]
        public static partial void PreflightPassed(
            ILogger logger, string repoKey, CloneTransport transport);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Clone preflight failed for '{RepoKey}' (transport: {Transport}): {Reason}")]
        public static partial void PreflightFailed(
            ILogger logger, string repoKey, CloneTransport transport, string reason);
    }
}

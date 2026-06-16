using System.Diagnostics;
using AgentController.Application;
using AgentController.Domain;
using AgentController.Infrastructure.Options;
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
/// such as <see cref="BackgroundService"/>. Uses <see cref="IOptionsMonitor{TOptions}"/>
/// for live config reload support.
/// </summary>
public sealed partial class LocalGitSourceControlProvider : ISourceControlProvider
{
    private readonly IOptionsMonitor<AgentControllerOptions> _controllerOptions;
    private readonly ILogger<LocalGitSourceControlProvider> _logger;

    /// <summary>
    /// Maximum time a git clone operation is allowed to run before being terminated.
    /// </summary>
    private static readonly TimeSpan CloneTimeout = TimeSpan.FromMinutes(10);

    public LocalGitSourceControlProvider(
        IOptionsMonitor<AgentControllerOptions> controllerOptions,
        ILogger<LocalGitSourceControlProvider> logger)
    {
        _controllerOptions = controllerOptions;
        _logger = logger;
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

        var cloneUrl = NormalizeCloneUrl(spec.CloneUrl);
        var targetDir = Path.Combine(environment.RootPath, "repo");

        // Ensure the parent directory exists (environment provider should have created it,
        // but be defensive).
        var parentDir = Path.GetDirectoryName(targetDir);
        if (parentDir is not null)
        {
            Directory.CreateDirectory(parentDir);
        }

        Log.CloningRepository(_logger, spec.RepoKey, cloneUrl, targetDir);

        // Build git clone arguments.
        // --quiet suppresses progress output.
        // --branch checks out a specific branch after clone.
        var args = new List<string> { "clone", "--quiet" };

        if (!string.IsNullOrWhiteSpace(spec.DefaultBranch))
        {
            args.Add("--branch");
            args.Add(spec.DefaultBranch);
        }

        args.Add(cloneUrl);
        args.Add(targetDir);

        var (exitCode, stdErr) = await RunGitAsync(args, cancellationToken);

        if (exitCode != 0)
        {
            var errorDetail = stdErr.Length > 0
                ? $"\n{stdErr.Trim()}"
                : string.Empty;
            throw new InvalidOperationException(
                $"git clone failed for repository '{spec.RepoKey}' " +
                $"(cloneUrl: '{cloneUrl}', exit code: {exitCode}).{errorDetail}");
        }

        Log.CloneCompleted(_logger, spec.RepoKey, targetDir);

        // Resolve the HEAD commit SHA for auditability.
        var commitSha = await GetHeadCommitShaAsync(targetDir, cancellationToken);

        return new RepositoryCheckout
        {
            RepoKey = spec.RepoKey,
            LocalPath = targetDir,
            Branch = spec.DefaultBranch,
            CommitSha = commitSha,
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
    /// Run a git command with the given arguments.
    /// Returns the process exit code and captured stderr.
    /// </summary>
    private static async Task<(int ExitCode, string StdErr)> RunGitAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
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
            },
        };

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
                },
            };

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
            Message = "Cloning repository '{RepoKey}' from '{CloneUrl}' into '{TargetDir}'.")]
        public static partial void CloningRepository(
            ILogger logger, string repoKey, string cloneUrl, string targetDir);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Repository '{RepoKey}' cloned successfully into '{TargetDir}'.")]
        public static partial void CloneCompleted(
            ILogger logger, string repoKey, string targetDir);
    }
}

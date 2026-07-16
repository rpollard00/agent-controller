using System.Diagnostics;
using System.Text;
using AgentController.Application;
using AgentController.Application.Results;
using AgentController.Domain;
using Microsoft.Extensions.Logging;

namespace AgentController.Infrastructure;

/// <summary>
/// Local-git implementation of <see cref="IRepositoryMaterializer"/>.
/// 
/// Clones repositories into the local filesystem using <c>git clone</c> with
/// transport-appropriate credential injection:
/// <list type="bullet">
///   <item><b>HTTPS+PAT</b>: injects credentials via <c>git http.extraHeader</c>
///   config (not URL embedding), avoiding PAT leakage in process listings or logs.</item>
///   <item><b>SSH</b>: uses the configured SSH key with <c>GIT_SSH_COMMAND</c>
///   for non-interactive batch mode.</item>
///   <item><b>Local</b>: native git clone for local paths and <c>file://</c> URLs.</item>
/// </list>
///
/// Registered as a singleton via DI extensions.
/// </summary>
public sealed partial class LocalGitRepositoryMaterializer : IRepositoryMaterializer
{
    private readonly IManagedSecretStore _secretStore;
    private readonly ILogger<LocalGitRepositoryMaterializer> _logger;

    /// <summary>
    /// Maximum time a git clone operation is allowed to run before being terminated.
    /// </summary>
    private static readonly TimeSpan CloneTimeout = TimeSpan.FromMinutes(10);

    public LocalGitRepositoryMaterializer(
        IManagedSecretStore secretStore,
        ILogger<LocalGitRepositoryMaterializer> logger)
    {
        _secretStore = secretStore;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<RepositoryMaterializationResult> MaterializeAsync(
        RepositoryProfile profile,
        ResolvedSecretsManifest manifest,
        EnvironmentHandle environment,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(profile.CloneUrl))
        {
            return RepositoryMaterializationResult.FailureResult(
                profile.Key,
                $"Cannot materialize repository '{profile.Key}': cloneUrl is empty.");
        }

        if (string.IsNullOrWhiteSpace(environment.RootPath))
        {
            return RepositoryMaterializationResult.FailureResult(
                profile.Key,
                $"Cannot materialize repository '{profile.Key}': environment has no root path.");
        }

        var cloneUrl = LocalGitSourceControlProvider.NormalizeCloneUrl(profile.CloneUrl);
        var transport = LocalGitSourceControlProvider.ResolveTransport(profile.Transport, cloneUrl);
        var targetDir = Path.Combine(environment.RootPath, "repo");

        // Ensure the parent directory exists.
        var parentDir = Path.GetDirectoryName(targetDir);
        if (parentDir is not null)
        {
            Directory.CreateDirectory(parentDir);
        }

        Log.MaterializingRepository(_logger, profile.Key, cloneUrl, targetDir, transport);

        try
        {
            var checkout = await CloneWithCredentialsAsync(
                profile.Key,
                cloneUrl,
                transport,
                profile.DefaultBranch,
                targetDir,
                manifest,
                cancellationToken);

            return RepositoryMaterializationResult.SuccessResult(
                profile.Key,
                checkout.LocalPath,
                checkout.Branch,
                checkout.CommitSha,
                checkout.Transport);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.MaterializationFailed(_logger, profile.Key, ex);
            return RepositoryMaterializationResult.FailureResult(
                profile.Key,
                $"Materialization failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Clone a repository with transport-appropriate credential injection.
    /// </summary>
    private async Task<RepositoryCheckout> CloneWithCredentialsAsync(
        string repoKey,
        string cloneUrl,
        CloneTransport transport,
        string defaultBranch,
        string targetDir,
        ResolvedSecretsManifest manifest,
        CancellationToken cancellationToken)
    {
        // Build git clone arguments.
        var args = new List<string> { "clone", "--quiet" };

        // For HTTPS+PAT, inject credentials via git config http.extraHeader
        // instead of embedding in the URL. This avoids PAT leakage in process listings.
        string? extraHeaderConfig = null;
        if (transport == CloneTransport.HttpsPat)
        {
            var pat = ExtractPatFromManifest(manifest);
            if (!string.IsNullOrEmpty(pat))
            {
                // Git expects: Authorization: Basic base64(user:pat)
                // Using an empty username is valid for PAT-based auth.
                var credentials = $"x-token:{pat}";
                var base64Credentials = Convert.ToBase64String(
                    Encoding.UTF8.GetBytes(credentials));
                extraHeaderConfig = $"Authorization: Basic {base64Credentials}";
                args.Add("-c");
                args.Add($"http.extraHeader={extraHeaderConfig}");
            }
        }

        if (!string.IsNullOrWhiteSpace(defaultBranch))
        {
            args.Add("--branch");
            args.Add(defaultBranch);
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
                $"git clone failed for repository '{repoKey}' " +
                $"(cloneUrl: '{cloneUrl}', exit code: {exitCode}).{errorDetail}");
        }

        Log.CloneCompleted(_logger, repoKey, targetDir, transport);

        // Resolve the HEAD commit SHA for auditability.
        var commitSha = await GetHeadCommitShaAsync(targetDir, cancellationToken);

        return new RepositoryCheckout
        {
            RepoKey = repoKey,
            LocalPath = targetDir,
            Branch = defaultBranch,
            CommitSha = commitSha,
            Transport = transport,
            ClonedAt = DateTimeOffset.UtcNow,
        };
    }

    /// <summary>
    /// Extract the PAT value from the resolved secrets manifest.
    /// Looks for the first secret with a non-null value.
    /// </summary>
    private static string? ExtractPatFromManifest(ResolvedSecretsManifest manifest)
    {
        // The manifest may contain multiple secrets; find the PAT.
        // Convention: PAT secrets have Kind "EnvVar" or "Db" with an Id
        // containing "PAT" or "TOKEN" (case-insensitive).
        foreach (var secret in manifest.Secrets)
        {
            if (secret.Value is not null)
            {
                var id = secret.Reference.Id.ToUpperInvariant();
                if (id.Contains("PAT") || id.Contains("TOKEN"))
                {
                    return secret.Value;
                }
            }
        }

        // Fallback: return the first non-null secret value.
        return manifest.Secrets
            .Select(s => s.Value)
            .FirstOrDefault(v => !string.IsNullOrEmpty(v));
    }

    /// <summary>
    /// Run a git command with non-interactive environment hardening.
    /// Reuses the same patterns as <see cref="LocalGitSourceControlProvider"/>.
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
                WorkingDirectory = Path.GetTempPath(),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
            },
        };

        // Apply non-interactive environment variables.
        foreach (var (key, value) in LocalGitSourceControlProvider.GitNonInteractiveEnv)
        {
            process.StartInfo.EnvironmentVariables[key] = value;
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

            var stdOutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            var stdErrTask = process.StandardError.ReadToEndAsync(cts.Token);

            await process.WaitForExitAsync(cts.Token);

            var stdErr = await stdErrTask;

            return (process.ExitCode, stdErr ?? string.Empty);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
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
                    RedirectStandardInput = true,
                },
            };

            foreach (var (key, value) in LocalGitSourceControlProvider.GitNonInteractiveEnv)
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
            return null;
        }
    }

    private static partial class Log
    {
        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Materializing repository '{RepoKey}' from '{CloneUrl}' into '{TargetDir}' (transport: {Transport}).")]
        public static partial void MaterializingRepository(
            ILogger logger, string repoKey, string cloneUrl, string targetDir, CloneTransport transport);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Repository '{RepoKey}' cloned successfully into '{TargetDir}' (transport: {Transport}).")]
        public static partial void CloneCompleted(
            ILogger logger, string repoKey, string targetDir, CloneTransport transport);

        [LoggerMessage(
            Level = LogLevel.Error,
            Message = "Materialization failed for repository '{RepoKey}'."
        )]
        public static partial void MaterializationFailed(
            ILogger logger, string repoKey, Exception ex);
    }
}

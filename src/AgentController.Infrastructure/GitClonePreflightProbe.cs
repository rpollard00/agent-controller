using System.Diagnostics;
using AgentController.Domain;

namespace AgentController.Infrastructure;

/// <summary>
/// Executes and classifies the non-cloning Git/SSH subprocesses used by clone preflight.
/// Credential resolution and materialization remain owned by the source-control provider.
/// </summary>
internal static class GitClonePreflightProbe
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);

    public static async Task<GitClonePreflightProbeResult> RunAsync(
        string cloneUrl,
        string? defaultBranch,
        CloneTransport transport,
        RepositoryCloneTransportResolution? resolution,
        bool disableCredentialHelpers,
        IReadOnlyDictionary<string, string?> baseEnvironment,
        IReadOnlyDictionary<string, string?>? credentialEnvironment,
        CancellationToken cancellationToken
    )
    {
        if (transport == CloneTransport.Ssh)
        {
            var sshCheck = await RunCommandAsync(
                "ssh",
                ["-V"],
                TimeSpan.FromSeconds(5),
                cancellationToken
            );
            if (sshCheck.ExitCode < 0)
            {
                return GitClonePreflightProbeResult.Failed(
                    ClonePreflightFailureCode.ToolUnavailable,
                    "SSH command not found. Install OpenSSH and ensure 'ssh' is on PATH."
                );
            }
        }

        var (exitCode, standardError) = await RunGitLsRemoteAsync(
            cloneUrl,
            defaultBranch,
            disableCredentialHelpers,
            baseEnvironment,
            credentialEnvironment,
            ProbeTimeout,
            cancellationToken
        );

        if (exitCode == 0)
        {
            return GitClonePreflightProbeResult.Passed();
        }

        var (code, reason) = ClassifyFailure(standardError, transport, resolution);
        return GitClonePreflightProbeResult.Failed(code, reason);
    }

    public static string DescribeCredential(RepositoryCloneTransportResolution? resolution)
    {
        if (resolution?.CredentialReference is not { } reference)
        {
            return "the configured credentials";
        }

        var type = resolution.CredentialSource switch
        {
            RepositoryCloneCredentialSource.SshKey => "SSH-key secret",
            RepositoryCloneCredentialSource.ConnectionPersonalAccessToken => "PAT secret",
            _ => "secret",
        };
        var version = reference.Version is null ? "latest version" : $"version {reference.Version}";
        return $"{type} '{reference.Name}' ({version})";
    }

    internal static (ClonePreflightFailureCode Code, string Reason) ClassifyFailure(
        string standardError,
        CloneTransport transport,
        RepositoryCloneTransportResolution? resolution
    )
    {
        var error = standardError.ToLowerInvariant();
        if (error.Contains("git ls-remote process error", StringComparison.Ordinal))
        {
            return (
                ClonePreflightFailureCode.ToolUnavailable,
                "The Git remote probe could not be started. Ensure 'git' is installed and on PATH."
            );
        }

        if (
            error.Contains("could not resolve host", StringComparison.Ordinal)
            || error.Contains("could not resolve hostname", StringComparison.Ordinal)
            || error.Contains("name or service not known", StringComparison.Ordinal)
            || error.Contains("network is unreachable", StringComparison.Ordinal)
            || error.Contains("connection timed out", StringComparison.Ordinal)
            || error.Contains("failed to connect", StringComparison.Ordinal)
            || error.Contains("connection refused", StringComparison.Ordinal)
            || error.Contains("timed out", StringComparison.Ordinal)
        )
        {
            return (
                ClonePreflightFailureCode.RemoteUnreachable,
                "The repository host could not be reached. Check the clone URL, DNS, network, and firewall settings."
            );
        }

        if (transport == CloneTransport.Local)
        {
            return (
                ClonePreflightFailureCode.RemoteRejected,
                "The local path exists but is not a readable Git repository. Verify the path and repository contents."
            );
        }

        var credential = DescribeCredential(resolution);
        if (
            error.Contains("load key", StringComparison.Ordinal)
            && (
                error.Contains("invalid format", StringComparison.Ordinal)
                || error.Contains("incorrect passphrase", StringComparison.Ordinal)
            )
        )
        {
            return (
                ClonePreflightFailureCode.CredentialInvalid,
                $"{credential} is not a valid usable private key. Verify its format and passphrase."
            );
        }

        if (
            error.Contains("authentication failed", StringComparison.Ordinal)
            || error.Contains("permission denied", StringComparison.Ordinal)
            || error.Contains("publickey", StringComparison.Ordinal)
            || error.Contains("access denied", StringComparison.Ordinal)
            || error.Contains("could not read username", StringComparison.Ordinal)
            || error.Contains("http 401", StringComparison.Ordinal)
            || error.Contains("http 403", StringComparison.Ordinal)
            || error.Contains("requested url returned error: 401", StringComparison.Ordinal)
            || error.Contains("requested url returned error: 403", StringComparison.Ordinal)
        )
        {
            var remediation =
                transport == CloneTransport.HttpsPat
                    ? "Verify the PAT is active, unexpired, and authorized to read this repository."
                    : "Verify the SSH public key is registered with the repository host and has read access.";
            return (
                ClonePreflightFailureCode.AuthenticationFailed,
                $"Authentication failed using {credential}. {remediation}"
            );
        }

        return (
            ClonePreflightFailureCode.RemoteRejected,
            $"The repository probe was rejected while using {credential}. Verify the clone URL and repository read permission."
        );
    }

    private static async Task<(int ExitCode, string StandardError)> RunGitLsRemoteAsync(
        string cloneUrl,
        string? defaultBranch,
        bool disableCredentialHelpers,
        IReadOnlyDictionary<string, string?> baseEnvironment,
        IReadOnlyDictionary<string, string?>? credentialEnvironment,
        TimeSpan timeout,
        CancellationToken cancellationToken
    )
    {
        var arguments = new List<string>();
        if (disableCredentialHelpers)
        {
            arguments.Add("-c");
            arguments.Add("credential.helper=");
        }

        arguments.Add("ls-remote");
        arguments.Add(cloneUrl);
        if (!string.IsNullOrWhiteSpace(defaultBranch))
        {
            arguments.Add($"refs/heads/{defaultBranch}");
        }

        using var process = CreateProcess("git", arguments, baseEnvironment, credentialEnvironment);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken
        );
        timeoutSource.CancelAfter(timeout);

        try
        {
            process.Start();
            var standardOutput = process.StandardOutput.ReadToEndAsync(timeoutSource.Token);
            var standardError = process.StandardError.ReadToEndAsync(timeoutSource.Token);
            await process.WaitForExitAsync(timeoutSource.Token);
            _ = await standardOutput;
            return (process.ExitCode, await standardError);
        }
        catch (OperationCanceledException)
            when (timeoutSource.IsCancellationRequested
                && !cancellationToken.IsCancellationRequested
            )
        {
            TryKill(process);
            return (-1, $"git ls-remote timed out after {timeout.TotalSeconds:F0}s.");
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
        catch (Exception ex)
        {
            return (-1, $"git ls-remote process error: {ex.Message}");
        }
    }

    private static async Task<(int ExitCode, string StandardError)> RunCommandAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken
    )
    {
        using var process = CreateProcess(fileName, arguments);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken
        );
        timeoutSource.CancelAfter(timeout);

        try
        {
            process.Start();
            var standardOutput = process.StandardOutput.ReadToEndAsync(timeoutSource.Token);
            var standardError = process.StandardError.ReadToEndAsync(timeoutSource.Token);
            await process.WaitForExitAsync(timeoutSource.Token);
            _ = await standardOutput;
            return (process.ExitCode, await standardError);
        }
        catch (OperationCanceledException)
            when (timeoutSource.IsCancellationRequested
                && !cancellationToken.IsCancellationRequested
            )
        {
            TryKill(process);
            return (-1, $"'{fileName}' timed out after {timeout.TotalSeconds:F0}s.");
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }

    private static Process CreateProcess(
        string fileName,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?>? baseEnvironment = null,
        IReadOnlyDictionary<string, string?>? additionalEnvironment = null
    )
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                WorkingDirectory = Path.GetTempPath(),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        ApplyEnvironment(process.StartInfo, baseEnvironment);
        ApplyEnvironment(process.StartInfo, additionalEnvironment);
        return process;
    }

    private static void ApplyEnvironment(
        ProcessStartInfo startInfo,
        IReadOnlyDictionary<string, string?>? environment
    )
    {
        if (environment is null)
        {
            return;
        }

        foreach (var (key, value) in environment)
        {
            startInfo.EnvironmentVariables[key] = value;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best effort after timeout or caller cancellation.
        }
    }
}

internal sealed record GitClonePreflightProbeResult(
    bool Success,
    ClonePreflightFailureCode? FailureCode,
    string Reason
)
{
    public static GitClonePreflightProbeResult Passed() => new(true, null, string.Empty);

    public static GitClonePreflightProbeResult Failed(
        ClonePreflightFailureCode failureCode,
        string reason
    ) => new(false, failureCode, reason);
}

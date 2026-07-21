using System.Diagnostics;
using AgentController.Domain.Secrets;

namespace AgentController.Infrastructure;

/// <summary>
/// Ephemeral Git/SSH configuration for one clone operation. The private key and
/// per-operation known-hosts file live in a restrictive temporary directory and are
/// removed after use. Passphrase-protected keys are loaded into a short-lived ssh-agent.
/// </summary>
internal sealed class GitSshCredentials : IDisposable
{
    internal const string KeyFileEnvironmentVariable = "AGENT_CONTROLLER_GIT_SSH_KEY_FILE";
    internal const string KnownHostsFileEnvironmentVariable =
        "AGENT_CONTROLLER_GIT_SSH_KNOWN_HOSTS_FILE";

    private readonly string _directoryPath;
    private readonly Process? _sshAgentProcess;
    private bool _disposed;

    private GitSshCredentials(
        string directoryPath,
        Process? sshAgentProcess,
        IReadOnlyDictionary<string, string?> environment
    )
    {
        _directoryPath = directoryPath;
        _sshAgentProcess = sshAgentProcess;
        Environment = environment;
    }

    /// <summary>Environment variables supplied only to the SSH Git subprocess.</summary>
    public IReadOnlyDictionary<string, string?> Environment { get; }

    /// <summary>Creates private, temporary SSH material for one Git operation.</summary>
    public static async Task<GitSshCredentials> CreateAsync(
        SshKeyPayload sshKey,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(sshKey);

        if (string.IsNullOrWhiteSpace(sshKey.PrivateKey))
        {
            throw new ArgumentException("A non-empty SSH private key is required.", nameof(sshKey));
        }

        if (string.IsNullOrWhiteSpace(sshKey.PublicKey))
        {
            throw new ArgumentException("A non-empty SSH public key is required.", nameof(sshKey));
        }

        var directoryPath = Path.Combine(
            Path.GetTempPath(),
            $"agent-controller-git-ssh-{Guid.NewGuid():N}"
        );
        Process? sshAgentProcess = null;

        try
        {
            CreatePrivateDirectory(directoryPath);

            var keyPath = Path.Combine(directoryPath, "identity");
            var knownHostsPath = Path.Combine(directoryPath, "known_hosts");
            var sshCommandPath = Path.Combine(
                directoryPath,
                OperatingSystem.IsWindows() ? "ssh-command.cmd" : "ssh-command.sh"
            );

            WritePrivateFile(keyPath, sshKey.PrivateKey, executable: false);
            // OpenSSH consults the adjacent public key when matching a passphrase-protected
            // identity file to the same key already loaded in the ephemeral agent.
            WritePrivateFile($"{keyPath}.pub", sshKey.PublicKey, executable: false);
            WritePrivateFile(knownHostsPath, string.Empty, executable: false);
            WritePrivateFile(sshCommandPath, CreateSshCommandScript(), executable: true);

            var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["GIT_SSH_COMMAND"] = QuoteCommandPath(sshCommandPath),
                ["GIT_SSH_VARIANT"] = "ssh",
                [KeyFileEnvironmentVariable] = keyPath,
                [KnownHostsFileEnvironmentVariable] = knownHostsPath,
            };

            if (!string.IsNullOrEmpty(sshKey.Passphrase))
            {
                var agentSocketPath = Path.Combine(directoryPath, "agent.sock");
                sshAgentProcess = await StartSshAgentAsync(
                    agentSocketPath,
                    cancellationToken
                );

                await LoadKeyIntoAgentAsync(
                    directoryPath,
                    keyPath,
                    agentSocketPath,
                    sshKey.Passphrase,
                    cancellationToken
                );
                environment["SSH_AUTH_SOCK"] = agentSocketPath;
            }

            return new GitSshCredentials(directoryPath, sshAgentProcess, environment);
        }
        catch
        {
            StopSshAgent(sshAgentProcess);
            DeleteDirectory(directoryPath);
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopSshAgent(_sshAgentProcess);
        DeleteDirectory(_directoryPath);
        GC.SuppressFinalize(this);
    }

    private static void CreatePrivateDirectory(string directoryPath)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(directoryPath);
            return;
        }

        Directory.CreateDirectory(
            directoryPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        );
    }

    private static void WritePrivateFile(string path, string content, bool executable)
    {
        File.WriteAllText(path, content);

        if (!OperatingSystem.IsWindows())
        {
            var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            if (executable)
            {
                mode |= UnixFileMode.UserExecute;
            }

            File.SetUnixFileMode(path, mode);
        }
    }

    private static async Task<Process> StartSshAgentAsync(
        string socketPath,
        CancellationToken cancellationToken
    )
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "ssh-agent",
                WorkingDirectory = Path.GetTempPath(),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        process.StartInfo.ArgumentList.Add("-D");
        process.StartInfo.ArgumentList.Add("-a");
        process.StartInfo.ArgumentList.Add(socketPath);

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        catch (Exception ex)
        {
            process.Dispose();
            throw new InvalidOperationException(
                "A passphrase-protected SSH key could not be prepared because ssh-agent failed to start.",
                ex
            );
        }

        try
        {
            var startedAt = Stopwatch.StartNew();
            while (!File.Exists(socketPath))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (process.HasExited)
                {
                    throw new InvalidOperationException(
                        "A passphrase-protected SSH key could not be prepared because ssh-agent exited."
                    );
                }

                if (startedAt.Elapsed >= TimeSpan.FromSeconds(5))
                {
                    throw new InvalidOperationException(
                        "A passphrase-protected SSH key could not be prepared because ssh-agent did not become ready."
                    );
                }

                await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
            }

            return process;
        }
        catch
        {
            StopSshAgent(process);
            throw;
        }
    }

    private static async Task LoadKeyIntoAgentAsync(
        string directoryPath,
        string keyPath,
        string socketPath,
        string passphrase,
        CancellationToken cancellationToken
    )
    {
        var passphrasePath = Path.Combine(directoryPath, "passphrase");
        var askPassPath = Path.Combine(
            directoryPath,
            OperatingSystem.IsWindows() ? "ssh-askpass.cmd" : "ssh-askpass.sh"
        );

        WritePrivateFile(passphrasePath, passphrase, executable: false);
        WritePrivateFile(askPassPath, CreateAskPassScript(), executable: true);

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "ssh-add",
                    WorkingDirectory = directoryPath,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };
            process.StartInfo.ArgumentList.Add(keyPath);
            process.StartInfo.EnvironmentVariables["SSH_AUTH_SOCK"] = socketPath;
            process.StartInfo.EnvironmentVariables["SSH_ASKPASS"] = askPassPath;
            process.StartInfo.EnvironmentVariables["SSH_ASKPASS_REQUIRE"] = "force";
            process.StartInfo.EnvironmentVariables["DISPLAY"] = "agent-controller:0";
            process.StartInfo.EnvironmentVariables["AGENT_CONTROLLER_GIT_SSH_PASSPHRASE_FILE"] =
                passphrasePath;

            try
            {
                process.Start();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "A passphrase-protected SSH key could not be prepared because ssh-add failed to start.",
                    ex
                );
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));

            try
            {
                var standardOutput = process.StandardOutput.ReadToEndAsync(timeout.Token);
                var standardError = process.StandardError.ReadToEndAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token);
                await Task.WhenAll(standardOutput, standardError);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                throw new InvalidOperationException(
                    "A passphrase-protected SSH key could not be prepared because ssh-add timed out."
                );
            }

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    "The stored SSH private key could not be loaded. Verify its format and passphrase."
                );
            }
        }
        finally
        {
            File.Delete(askPassPath);
            File.Delete(passphrasePath);
        }
    }

    private static string CreateSshCommandScript() =>
        OperatingSystem.IsWindows()
            ? "@echo off\r\n"
                + $"ssh -i \"%{KeyFileEnvironmentVariable}%\" -o IdentitiesOnly=yes "
                + "-o BatchMode=yes -o StrictHostKeyChecking=accept-new "
                + $"-o UserKnownHostsFile=\"%{KnownHostsFileEnvironmentVariable}%\" %*\r\n"
            : "#!/bin/sh\n"
                + $"exec ssh -i \"${KeyFileEnvironmentVariable}\" -o IdentitiesOnly=yes "
                + "-o BatchMode=yes -o StrictHostKeyChecking=accept-new "
                + $"-o UserKnownHostsFile=\"${KnownHostsFileEnvironmentVariable}\" \"$@\"\n";

    private static string CreateAskPassScript() =>
        OperatingSystem.IsWindows()
            ? "@echo off\r\n"
                + "type \"%AGENT_CONTROLLER_GIT_SSH_PASSPHRASE_FILE%\"\r\n"
            : "#!/bin/sh\n"
                + "cat \"$AGENT_CONTROLLER_GIT_SSH_PASSPHRASE_FILE\"\n";

    private static string QuoteCommandPath(string path) =>
        OperatingSystem.IsWindows()
            ? $"\"{path}\""
            : $"'{path.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";

    private static void StopSshAgent(Process? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(milliseconds: 2_000);
            }
        }
        catch
        {
            // Best-effort cleanup. The agent is dedicated to this one operation.
        }
        finally
        {
            process.Dispose();
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
            // Best effort after timeout.
        }
    }

    private static void DeleteDirectory(string directoryPath)
    {
        try
        {
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup. Restrictive permissions protect any leftover material.
        }
    }
}

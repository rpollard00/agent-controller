using System.Diagnostics;
using AgentController.Application;
using AgentController.Domain;
using AgentController.Infrastructure.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentController.Infrastructure;

/// <summary>
/// Local-workspace implementation of <see cref="IEnvironmentProvider"/>.
///
/// Creates per-run workspace directories under <c>{runRoot}/{runId}/</c> with
/// subdirectories for the repository checkout, context files, logs, artifacts,
/// and results. Supports command execution and configurable workspace retention.
///
/// Registered as a singleton so it is safe for consumption by singleton consumers
/// such as <see cref="BackgroundService"/>. Uses <see cref="IOptionsMonitor{TOptions}"/>
/// for live config reload support.
/// </summary>
public sealed partial class LocalWorkspaceEnvironmentProvider : IEnvironmentProvider
{
    private readonly IOptionsMonitor<AgentControllerOptions> _controllerOptions;
    private readonly ILogger<LocalWorkspaceEnvironmentProvider> _logger;

    /// <summary>
    /// Maximum time a command execution is allowed to run before being terminated.
    /// </summary>
    private static readonly TimeSpan DefaultCommandTimeout = TimeSpan.FromMinutes(30);

    public LocalWorkspaceEnvironmentProvider(
        IOptionsMonitor<AgentControllerOptions> controllerOptions,
        ILogger<LocalWorkspaceEnvironmentProvider> logger
    )
    {
        _controllerOptions = controllerOptions;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<EnvironmentHandle> CreateAsync(
        EnvironmentSpec spec,
        CancellationToken cancellationToken
    )
    {
        var runRoot = ResolveRunRoot(spec);
        var envPath = Path.Combine(runRoot, spec.RunId);

        Log.CreatingEnvironment(_logger, spec.RunId, envPath);

        // Create the per-run workspace directory structure.
        Directory.CreateDirectory(envPath);
        Directory.CreateDirectory(Path.Combine(envPath, "repo"));
        Directory.CreateDirectory(Path.Combine(envPath, "context"));
        Directory.CreateDirectory(Path.Combine(envPath, "logs"));
        Directory.CreateDirectory(Path.Combine(envPath, "artifacts"));
        Directory.CreateDirectory(Path.Combine(envPath, "result"));

        Log.EnvironmentCreated(_logger, spec.RunId, envPath);

        var handle = new EnvironmentHandle
        {
            Id = $"local-{spec.RunId}",
            ProviderType = "LocalWorkspace",
            RootPath = envPath,
            Status = "created",
        };

        return Task.FromResult(handle);
    }

    /// <inheritdoc />
    public async Task<CommandResult> ExecuteAsync(
        EnvironmentHandle handle,
        CommandSpec command,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(command.Command))
        {
            throw new InvalidOperationException("Cannot execute command: command is empty.");
        }

        var workingDirectory = ResolveWorkingDirectory(handle, command);

        if (!Directory.Exists(workingDirectory))
        {
            throw new InvalidOperationException(
                $"Cannot execute command: working directory does not exist: '{workingDirectory}'."
            );
        }

        var timeout = command.Timeout ?? DefaultCommandTimeout;

        Log.ExecutingCommand(_logger, command.Command, workingDirectory);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = command.Command,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

        foreach (var arg in command.Arguments)
        {
            process.StartInfo.ArgumentList.Add(arg);
        }

        // Set environment variables if provided.
        if (command.EnvironmentVariables is { Count: > 0 })
        {
            foreach (var (key, value) in command.EnvironmentVariables)
            {
                process.StartInfo.Environment[key] = value;
            }
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        var startedAt = DateTimeOffset.UtcNow;

        try
        {
            process.Start();

            var stdOutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            var stdErrTask = process.StandardError.ReadToEndAsync(cts.Token);

            await process.WaitForExitAsync(cts.Token);

            var stdOut = await stdOutTask;
            var stdErr = await stdErrTask;
            var duration = DateTimeOffset.UtcNow - startedAt;

            Log.CommandCompleted(
                _logger,
                command.Command,
                process.ExitCode,
                (int)duration.TotalSeconds
            );

            return new CommandResult
            {
                ExitCode = process.ExitCode,
                StdOut = stdOut,
                StdErr = stdErr,
                Duration = duration,
                TimedOut = false,
            };
        }
        catch (OperationCanceledException)
            when (cts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            { /* best effort */
            }

            var duration = DateTimeOffset.UtcNow - startedAt;

            Log.CommandTimedOut(_logger, command.Command, (int)duration.TotalSeconds);

            return new CommandResult
            {
                ExitCode = -1,
                StdOut = null,
                StdErr = "Command timed out.",
                Duration = duration,
                TimedOut = true,
            };
        }
    }

    /// <inheritdoc />
    public Task DestroyAsync(EnvironmentHandle handle, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(handle.RootPath))
        {
            return Task.CompletedTask;
        }

        if (!Directory.Exists(handle.RootPath))
        {
            return Task.CompletedTask;
        }

        var options = _controllerOptions.CurrentValue;

        // Check retention configuration before destroying.
        // If the environment status indicates a successful run and
        // retainSuccessfulRuns is true, skip destruction.
        // Similarly for failed runs.
        //
        // Note: the environment provider doesn't directly know the run status.
        // Retention policy is checked by the caller (worker). This method
        // is called only when the caller has determined the workspace should
        // be cleaned up.

        Log.DestroyingEnvironment(_logger, handle.RootPath);

        try
        {
            Directory.Delete(handle.RootPath, recursive: true);
            Log.EnvironmentDestroyed(_logger, handle.RootPath);
        }
        catch (Exception ex)
        {
            Log.DestroyFailed(_logger, handle.RootPath, ex);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Resolve the run root path from configuration, expanding tildes.
    /// </summary>
    private string ResolveRunRoot(EnvironmentSpec spec)
    {
        var raw = spec.RootPath;
        if (string.IsNullOrWhiteSpace(raw))
        {
            raw = spec.RuntimeEnvironmentProfile?.EnvironmentSettings.WorkspaceRoot;
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            raw = _controllerOptions.CurrentValue.RunRoot;
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new InvalidOperationException(
                "Cannot create environment: runRoot is not configured. "
                    + "Set 'agentController:runRoot' in configuration."
            );
        }

        return ExpandTilde(raw);
    }

    /// <summary>
    /// Resolve the absolute working directory for a command execution.
    /// Uses the environment root path as the base and joins any
    /// command-specified relative working directory.
    /// </summary>
    private static string ResolveWorkingDirectory(EnvironmentHandle handle, CommandSpec command)
    {
        if (string.IsNullOrWhiteSpace(command.WorkingDirectory))
        {
            return handle.RootPath;
        }

        if (Path.IsPathRooted(command.WorkingDirectory))
        {
            return command.WorkingDirectory;
        }

        return Path.Combine(handle.RootPath, command.WorkingDirectory);
    }

    /// <summary>
    /// Expand a leading tilde (~) in a path to the user's home directory.
    /// </summary>
    internal static string ExpandTilde(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return path;

        if (!path.StartsWith('~'))
            return path;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (path.Length == 1)
            return home;

        // Handle ~/path
        if (path[1] == '/' || path[1] == '\\')
        {
            return Path.Combine(home, path[2..]);
        }

        return path;
    }

    private static partial class Log
    {
        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Creating local environment for run '{RunId}' at '{EnvPath}'."
        )]
        public static partial void CreatingEnvironment(
            ILogger logger,
            string runId,
            string envPath
        );

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Local environment for run '{RunId}' created at '{EnvPath}'."
        )]
        public static partial void EnvironmentCreated(ILogger logger, string runId, string envPath);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Executing command '{Command}' in '{WorkingDirectory}'."
        )]
        public static partial void ExecutingCommand(
            ILogger logger,
            string command,
            string workingDirectory
        );

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Command '{Command}' completed with exit code {ExitCode} in {DurationSeconds}s."
        )]
        public static partial void CommandCompleted(
            ILogger logger,
            string command,
            int exitCode,
            int durationSeconds
        );

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Command '{Command}' timed out after {DurationSeconds}s."
        )]
        public static partial void CommandTimedOut(
            ILogger logger,
            string command,
            int durationSeconds
        );

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Destroying local environment at '{EnvPath}'."
        )]
        public static partial void DestroyingEnvironment(ILogger logger, string envPath);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Local environment at '{EnvPath}' destroyed."
        )]
        public static partial void EnvironmentDestroyed(ILogger logger, string envPath);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Failed to destroy local environment at '{EnvPath}'."
        )]
        public static partial void DestroyFailed(ILogger logger, string envPath, Exception ex);
    }
}

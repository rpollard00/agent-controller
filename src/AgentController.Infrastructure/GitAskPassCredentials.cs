namespace AgentController.Infrastructure;

/// <summary>
/// Ephemeral, process-local Git askpass configuration for HTTPS PAT authentication.
/// The PAT is kept out of clone URLs, command arguments, and logs. The helper and token
/// file live in a private temporary directory that is removed after the Git operation.
/// </summary>
internal sealed class GitAskPassCredentials : IDisposable
{
    internal const string TokenFileEnvironmentVariable = "AGENT_CONTROLLER_GIT_PAT_FILE";

    private readonly string _directoryPath;
    private bool _disposed;

    private GitAskPassCredentials(
        string directoryPath,
        IReadOnlyDictionary<string, string?> environment
    )
    {
        _directoryPath = directoryPath;
        Environment = environment;
    }

    /// <summary>Environment variables supplied only to the credentialed Git subprocess.</summary>
    public IReadOnlyDictionary<string, string?> Environment { get; }

    /// <summary>Creates private, temporary askpass material for one Git operation.</summary>
    public static GitAskPassCredentials Create(string personalAccessToken)
    {
        if (string.IsNullOrWhiteSpace(personalAccessToken))
        {
            throw new ArgumentException("A non-empty PAT is required.", nameof(personalAccessToken));
        }

        var directoryPath = Path.Combine(
            Path.GetTempPath(),
            $"agent-controller-git-credentials-{Guid.NewGuid():N}"
        );

        try
        {
            if (OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(directoryPath);
            }
            else
            {
                Directory.CreateDirectory(
                    directoryPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                );
            }

            var tokenPath = Path.Combine(directoryPath, "token");
            var askPassPath = Path.Combine(
                directoryPath,
                OperatingSystem.IsWindows() ? "askpass.cmd" : "askpass.sh"
            );

            File.WriteAllText(tokenPath, personalAccessToken);
            File.WriteAllText(askPassPath, CreateAskPassScript());

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    tokenPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite
                );
                File.SetUnixFileMode(
                    askPassPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                );
            }

            return new GitAskPassCredentials(
                directoryPath,
                new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["GIT_ASKPASS"] = askPassPath,
                    ["GIT_ASKPASS_REQUIRE"] = "force",
                    [TokenFileEnvironmentVariable] = tokenPath,
                }
            );
        }
        catch
        {
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
        DeleteDirectory(_directoryPath);
        GC.SuppressFinalize(this);
    }

    private static string CreateAskPassScript() =>
        OperatingSystem.IsWindows()
            ? "@echo off\r\n"
                + "echo %~1 | findstr /I \"username\" >nul\r\n"
                + "if not errorlevel 1 (\r\n"
                + "  echo x-token-auth\r\n"
                + ") else (\r\n"
                + $"  type \"%{TokenFileEnvironmentVariable}%\"\r\n"
                + ")\r\n"
            : "#!/bin/sh\n"
                + "case \"$1\" in\n"
                + "  *[Uu]sername*) printf '%s\\n' 'x-token-auth' ;;\n"
                + $"  *) cat \"${TokenFileEnvironmentVariable}\" ;;\n"
                + "esac\n";

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
            // Best-effort cleanup. The directory name contains no credential data,
            // and restrictive permissions prevent other users from reading leftovers.
        }
    }
}

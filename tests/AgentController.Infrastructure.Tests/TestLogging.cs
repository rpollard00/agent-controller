using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentController.Infrastructure.Tests;

/// <summary>
/// Minimal test logging helper — no console provider, Warning minimum level.
///
/// Duplicated per test project (no shared test-utility project).
/// Use this instead of <c>AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning))</c>
/// to prevent any console output on green test runs.
/// </summary>
internal static class TestLogging
{
    /// <summary>
    /// Configures silent logging for test service collections:
    /// clears all providers (including Console) and pins the default minimum level to Warning.
    /// </summary>
    public static void AddSilentLogging(this IServiceCollection services)
    {
        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.SetMinimumLevel(LogLevel.Warning);
        });
    }

    /// <summary>
    /// Configures a silent <see cref="ILoggerFactory"/> for tests:
    /// no providers added, Warning minimum level.
    /// </summary>
    public static ILoggerFactory CreateSilentLoggerFactory()
    {
        return LoggerFactory.Create(builder =>
        {
            builder.ClearProviders();
            builder.SetMinimumLevel(LogLevel.Warning);
        });
    }
}

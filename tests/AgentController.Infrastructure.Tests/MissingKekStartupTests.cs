using AgentController.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentController.Infrastructure.Tests;

/// <summary>
/// Tests for the loud-failure behavior when KEK is not configured
/// and secrets:provider=Db (the default).
/// </summary>
public sealed class MissingKekStartupTests
{
    /// <summary>
    /// When KEK is missing, AddAgentControllerNamedSecrets emits a critical
    /// log line containing 'KEK' and actionable guidance, then throws.
    /// Verified by xUnit capture of the log provider.
    /// </summary>
    [Fact]
    public void AddAgentControllerNamedSecrets_NoKek_EmitsCriticalLogAndThrows()
    {
        // Arrange — configure secrets:provider=Db with NO KEK configured.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["secrets:provider"] = "Db",
                // Deliberately omit: secrets:keyEncryptionKey:file:filePath
                // Deliberately omit: AGENT_CONTROLLER_SECRET_KEK_FILE_PATH
            })
            .Build();

        var capturedLogs = new List<CapturedLogEntry>();
        var testProvider = new InMemoryLoggerProvider(capturedLogs);

        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.SetMinimumLevel(LogLevel.Critical);
            builder.AddProvider(testProvider);
        });

        // Act & Assert — registration must throw and emit a critical log.
        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddAgentControllerNamedSecrets(config)
        );

        // Assert the exception message names the KEK configuration key.
        Assert.Contains("KEK", exception.Message);
        Assert.Contains("secrets:keyEncryptionKey:file:filePath", exception.Message);
        Assert.Contains("AGENT_CONTROLLER_SECRET_KEK_FILE_PATH", exception.Message);

        // Assert the critical log was emitted.
        Assert.NotEmpty(capturedLogs);

        var criticalLog = capturedLogs.FirstOrDefault(l => l.Level == LogLevel.Critical);
        Assert.NotNull(criticalLog);

        // The log must contain 'KEK' and actionable setup guidance.
        var message = criticalLog.Message;
        Assert.Contains("KEK", message);

        // Verify actionable guidance is present.
        Assert.Contains("openssl", message);
        Assert.Contains("AGENT_CONTROLLER_SECRET_KEK_FILE_PATH", message);
        Assert.Contains("secrets:keyEncryptionKey:file:filePath", message);
    }

    /// <summary>
    /// The critical log is emitted BEFORE the exception is thrown.
    /// This is verified by checking the log is captured when the exception is caught.
    /// </summary>
    [Fact]
    public void AddAgentControllerNamedSecrets_NoKek_CriticalLogEmittedBeforeException()
    {
        // Arrange
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["secrets:provider"] = "Db",
            })
            .Build();

        var capturedLogs = new List<CapturedLogEntry>();
        var testProvider = new InMemoryLoggerProvider(capturedLogs);

        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.SetMinimumLevel(LogLevel.Critical);
            builder.AddProvider(testProvider);
        });

        // Act — catch the exception; the log should already be captured.
        bool threw = false;
        try
        {
            services.AddAgentControllerNamedSecrets(config);
        }
        catch (InvalidOperationException)
        {
            threw = true;
        }

        // Assert
        Assert.True(threw, "Expected an InvalidOperationException to be thrown.");

        // The critical log must be captured (it was emitted before the throw).
        var criticalLogs = capturedLogs.Where(l => l.Level == LogLevel.Critical).ToList();
        Assert.NotEmpty(criticalLogs);

        var message = criticalLogs[0].Message;
        Assert.Contains("KEK", message);
    }

    // ─── Test helpers ──────────────────────────────────────────

    private sealed record CapturedLogEntry(
        LogLevel Level,
        string Message,
        string Category
    );

    /// <summary>
    /// In-memory ILoggerProvider that captures log entries for assertion.
    /// </summary>
    private sealed class InMemoryLoggerProvider : ILoggerProvider
    {
        private readonly List<CapturedLogEntry> _logs;

        public InMemoryLoggerProvider(List<CapturedLogEntry> logs)
        {
            _logs = logs;
        }

        public ILogger CreateLogger(string categoryName)
            => new InMemoryLogger(categoryName, _logs);

        public void Dispose() { }
    }

    private sealed class InMemoryLogger : ILogger
    {
        private readonly string _categoryName;
        private readonly List<CapturedLogEntry> _logs;

        public InMemoryLogger(string categoryName, List<CapturedLogEntry> logs)
        {
            _categoryName = categoryName;
            _logs = logs;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            // Capture the formatted message.
            var message = formatter(state, exception);
            _logs.Add(new CapturedLogEntry(logLevel, message, _categoryName));
        }
    }
}

using AgentController.Infrastructure;
using AgentController.Infrastructure.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentController.Infrastructure.Tests;

/// <summary>
/// Tests for <see cref="AzureDevOpsBoardStateStartupValidator"/>.
/// Verifies that the validator skips validation for non-ADO providers,
/// respects the skip environment variable, and handles missing config gracefully.
/// </summary>
public class AzureDevOpsBoardStateStartupValidatorTests
{
    private static IOptions<WorkSourceOptions> CreateWorkSourceOptions(
        string provider = "AzureDevOpsBoards",
        string? organizationUrl = null,
        string? project = null,
        string? activeState = "Active",
        string? completedState = "Resolved",
        IReadOnlyList<string>? eligibleStates = null,
        string? workItemType = null)
    {
        return global::Microsoft.Extensions.Options.Options.Create(new WorkSourceOptions
        {
            Provider = provider,
            OrganizationUrl = organizationUrl,
            Project = project,
            ActiveState = activeState,
            CompletedState = completedState,
            EligibleStates = eligibleStates ?? ["New"],
            WorkItemType = workItemType ?? WorkSourceOptions.DefaultWorkItemType,
        });
    }

    private static IOptions<AzureDevOpsBoardsOptions> CreateBoardsOptions(string? pat = null)
    {
        return global::Microsoft.Extensions.Options.Options.Create(new AzureDevOpsBoardsOptions
        {
            PersonalAccessToken = pat ?? string.Empty,
        });
    }

    [Fact]
    public async Task StartAsync_NonAdoProvider_SkipsValidation()
    {
        // When the work source provider is not AzureDevOpsBoards,
        // the validator should skip without error.
        var validator = new AzureDevOpsBoardStateStartupValidator(
            CreateWorkSourceOptions(provider: "LocalFake"),
            CreateBoardsOptions(),
            NullLogger<AzureDevOpsBoardStateStartupValidator>.Instance);

        await validator.StartAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartAsync_SkipEnvVarSet_SkipsValidation()
    {
        var original = Environment.GetEnvironmentVariable("AGENT_CONTROLLER_SKIP_ADO_STATE_VALIDATION");
        try
        {
            Environment.SetEnvironmentVariable("AGENT_CONTROLLER_SKIP_ADO_STATE_VALIDATION", "1");

            var validator = new AzureDevOpsBoardStateStartupValidator(
                CreateWorkSourceOptions(),
                CreateBoardsOptions(),
                NullLogger<AzureDevOpsBoardStateStartupValidator>.Instance);

            await validator.StartAsync(CancellationToken.None);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AGENT_CONTROLLER_SKIP_ADO_STATE_VALIDATION", original);
        }
    }

    [Fact]
    public async Task StartAsync_MissingPat_SkipsValidation()
    {
        // When no PAT is configured, the validator should skip without throwing.
        var validator = new AzureDevOpsBoardStateStartupValidator(
            CreateWorkSourceOptions(),
            CreateBoardsOptions(pat: string.Empty),
            NullLogger<AzureDevOpsBoardStateStartupValidator>.Instance);

        await validator.StartAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartAsync_MissingOrganizationUrl_SkipsValidation()
    {
        var envName = "TEST_ADO_PAT_STATE_VALIDATOR";
        try
        {
            Environment.SetEnvironmentVariable(envName, "test-pat");

            var validator = new AzureDevOpsBoardStateStartupValidator(
                CreateWorkSourceOptions(organizationUrl: null),
                CreateBoardsOptions(pat: $"ENV:{envName}"),
                NullLogger<AzureDevOpsBoardStateStartupValidator>.Instance);

            await validator.StartAsync(CancellationToken.None);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, null);
        }
    }

    [Fact]
    public async Task StartAsync_MissingProject_SkipsValidation()
    {
        var envName = "TEST_ADO_PAT_STATE_VALIDATOR2";
        try
        {
            Environment.SetEnvironmentVariable(envName, "test-pat");

            var validator = new AzureDevOpsBoardStateStartupValidator(
                CreateWorkSourceOptions(project: null),
                CreateBoardsOptions(pat: $"ENV:{envName}"),
                NullLogger<AzureDevOpsBoardStateStartupValidator>.Instance);

            await validator.StartAsync(CancellationToken.None);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, null);
        }
    }

    [Fact]
    public async Task StartAsync_EnvPatResolutionFailed_SkipsValidation()
    {
        // When the PAT references a missing env var, the validator should skip.
        var validator = new AzureDevOpsBoardStateStartupValidator(
            CreateWorkSourceOptions(
                organizationUrl: "https://dev.azure.com/testorg",
                project: "TestProject"),
            CreateBoardsOptions(pat: "ENV:NONEXISTENT_VAR_XYZ"),
            NullLogger<AzureDevOpsBoardStateStartupValidator>.Instance);

        await validator.StartAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StopAsync_CompletesImmediately()
    {
        var validator = new AzureDevOpsBoardStateStartupValidator(
            CreateWorkSourceOptions(),
            CreateBoardsOptions(),
            NullLogger<AzureDevOpsBoardStateStartupValidator>.Instance);

        var task = validator.StopAsync(CancellationToken.None);

        Assert.True(task.IsCompleted);
        Assert.Equal(TaskStatus.RanToCompletion, task.Status);
    }

    [Fact]
    public void WorkSourceOptions_DefaultWorkItemType_IsUserStory()
    {
        var options = new WorkSourceOptions();
        Assert.Equal("User Story", options.WorkItemType);
    }

    [Fact]
    public void WorkSourceOptions_WorkItemType_CanBeConfigured()
    {
        var options = new WorkSourceOptions
        {
            WorkItemType = "Task",
        };

        Assert.Equal("Task", options.WorkItemType);
    }

    /// <summary>
    /// Minimal null logger for unit tests.
    /// </summary>
    private sealed class NullLogger<T> : ILogger<T>, ILogger
    {
        public static NullLogger<T> Instance { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null!;
        public bool IsEnabled(LogLevel level) => false;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
    }
}

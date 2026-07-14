using AgentController.Application;
using AgentController.Domain;
using AgentController.Infrastructure;
using AgentController.Infrastructure.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentController.Infrastructure.Tests;

/// <summary>
/// Tests for <see cref="AzureDevOpsBoardStateStartupValidator"/>.
/// Verifies that the validator skips validation for non-ADO providers,
/// respects the skip environment variable, handles missing config gracefully,
/// and correctly rejects invalid ActiveState/CompletedState/CompletedStates
/// and duplicate ActiveState/CompletedState values.
/// </summary>
public class AzureDevOpsBoardStateStartupValidatorTests
{
    // ═══════════════════════════════════════════════════════════════
    // Skip-path tests (no ADO client interaction needed)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task StartAsync_NonAdoProvider_SkipsValidation()
    {
        var validator = CreateValidator(
            workSource: CreateWorkSourceOptions(provider: "LocalFake"));

        await validator.StartAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartAsync_SkipEnvVarSet_SkipsValidation()
    {
        var original = Environment.GetEnvironmentVariable("AGENT_CONTROLLER_SKIP_ADO_STATE_VALIDATION");
        try
        {
            Environment.SetEnvironmentVariable("AGENT_CONTROLLER_SKIP_ADO_STATE_VALIDATION", "1");

            var validator = CreateValidator();

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
        var validator = CreateValidator(
            boards: CreateBoardsOptions(pat: string.Empty));

        await validator.StartAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartAsync_MissingOrganizationUrl_SkipsValidation()
    {
        var envName = "TEST_ADO_PAT_STATE_VALIDATOR";
        try
        {
            Environment.SetEnvironmentVariable(envName, "test-pat");

            var validator = CreateValidator(
                workSource: CreateWorkSourceOptions(organizationUrl: null),
                boards: CreateBoardsOptions(pat: $"ENV:{envName}"));

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

            var validator = CreateValidator(
                workSource: CreateWorkSourceOptions(project: null),
                boards: CreateBoardsOptions(pat: $"ENV:{envName}"));

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
        var validator = CreateValidator(
            workSource: CreateWorkSourceOptions(
                organizationUrl: "https://dev.azure.com/testorg",
                project: "TestProject"),
            boards: CreateBoardsOptions(pat: "ENV:NONEXISTENT_VAR_XYZ"));

        await validator.StartAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StopAsync_CompletesImmediately()
    {
        var validator = CreateValidator();

        var task = validator.StopAsync(CancellationToken.None);

        Assert.True(task.IsCompleted);
        Assert.Equal(TaskStatus.RanToCompletion, task.Status);
    }

    // ═══════════════════════════════════════════════════════════════
    // Validation tests (mock IAzureDevOpsBoardsClient)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task StartAsync_ValidStates_PassesValidation()
    {
        // Arrange: mock client returns valid Agile states
        var mockClient = new FakeAzureDevOpsBoardsClient
        {
            ValidStates = ["New", "Approved", "InProgress", "Resolved", "Closed"],
        };

        var validator = CreateValidatorWithMockClient(
            mockClient,
            workSource: CreateWorkSourceOptions(
                organizationUrl: "https://dev.azure.com/testorg",
                project: "TestProject",
                activeState: "Active", // Not in valid states, but we'll test below
                completedState: "Resolved",
                completedStates: ["Resolved"]));

        // ActiveState "Active" is NOT in the mock valid states, so this should fail.
        // Let's use "Resolved" for ActiveState to test the success path.
        var validatorSuccess = CreateValidatorWithMockClient(
            mockClient,
            workSource: CreateWorkSourceOptions(
                organizationUrl: "https://dev.azure.com/testorg",
                project: "TestProject",
                activeState: "InProgress",
                completedState: "Resolved",
                completedStates: ["Resolved"]));

        // Act & Assert: should not throw
        await validatorSuccess.StartAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartAsync_InvalidActiveState_ThrowsInvalidOperationException()
    {
        var mockClient = new FakeAzureDevOpsBoardsClient
        {
            ValidStates = ["New", "Approved", "InProgress", "Resolved", "Closed"],
        };

        var validator = CreateValidatorWithMockClient(
            mockClient,
            workSource: CreateWorkSourceOptions(
                organizationUrl: "https://dev.azure.com/testorg",
                project: "TestProject",
                activeState: "Active", // NOT in valid states
                completedState: "Resolved",
                completedStates: ["Resolved"]));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => validator.StartAsync(CancellationToken.None));

        Assert.Contains("ActiveState 'Active'", ex.Message);
        Assert.Contains("not a valid System.State value", ex.Message);
        Assert.Contains("TestProject", ex.Message);
    }

    [Fact]
    public async Task StartAsync_InvalidCompletedState_ThrowsInvalidOperationException()
    {
        var mockClient = new FakeAzureDevOpsBoardsClient
        {
            ValidStates = ["New", "Approved", "InProgress", "Resolved", "Closed"],
        };

        var validator = CreateValidatorWithMockClient(
            mockClient,
            workSource: CreateWorkSourceOptions(
                organizationUrl: "https://dev.azure.com/testorg",
                project: "TestProject",
                activeState: "InProgress",
                completedState: "Done", // NOT in valid states
                completedStates: ["Resolved"]));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => validator.StartAsync(CancellationToken.None));

        Assert.Contains("CompletedState 'Done'", ex.Message);
        Assert.Contains("not a valid System.State value", ex.Message);
    }

    [Fact]
    public async Task StartAsync_InvalidCompletedStates_ThrowsInvalidOperationException()
    {
        var mockClient = new FakeAzureDevOpsBoardsClient
        {
            ValidStates = ["New", "Approved", "InProgress", "Resolved", "Closed"],
        };

        var validator = CreateValidatorWithMockClient(
            mockClient,
            workSource: CreateWorkSourceOptions(
                organizationUrl: "https://dev.azure.com/testorg",
                project: "TestProject",
                activeState: "InProgress",
                completedState: "Resolved",
                completedStates: ["New", "Queued"])); // "Queued" NOT in valid states

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => validator.StartAsync(CancellationToken.None));

        Assert.Contains("CompletedStates value 'Queued'", ex.Message);
        Assert.Contains("not a valid System.State value", ex.Message);
    }

    [Fact]
    public async Task StartAsync_MultipleInvalidStates_ReportsAllFailures()
    {
        var mockClient = new FakeAzureDevOpsBoardsClient
        {
            ValidStates = ["New", "Approved", "InProgress", "Resolved", "Closed"],
        };

        var validator = CreateValidatorWithMockClient(
            mockClient,
            workSource: CreateWorkSourceOptions(
                organizationUrl: "https://dev.azure.com/testorg",
                project: "TestProject",
                activeState: "Active", // invalid
                completedState: "Done", // invalid
                completedStates: ["New", "Queued"])); // "Queued" invalid

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => validator.StartAsync(CancellationToken.None));

        // Should report all three failures
        Assert.Contains("ActiveState 'Active'", ex.Message);
        Assert.Contains("CompletedState 'Done'", ex.Message);
        Assert.Contains("CompletedStates value 'Queued'", ex.Message);
    }

    [Fact]
    public async Task StartAsync_ActiveStateEqualsCompletedState_ThrowsInvalidOperationException()
    {
        var mockClient = new FakeAzureDevOpsBoardsClient
        {
            ValidStates = ["New", "Approved", "InProgress", "Resolved", "Closed"],
        };

        var validator = CreateValidatorWithMockClient(
            mockClient,
            workSource: CreateWorkSourceOptions(
                organizationUrl: "https://dev.azure.com/testorg",
                project: "TestProject",
                activeState: "Resolved",
                completedState: "Resolved",
                completedStates: ["Resolved"]));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => validator.StartAsync(CancellationToken.None));

        Assert.Contains("ActiveState and CompletedState must be distinct", ex.Message);
    }

    [Fact]
    public async Task StartAsync_ActiveStateEqualsCompletedStateCaseInsensitive_ThrowsInvalidOperationException()
    {
        var mockClient = new FakeAzureDevOpsBoardsClient
        {
            ValidStates = ["New", "Approved", "InProgress", "Resolved", "Closed"],
        };

        var validator = CreateValidatorWithMockClient(
            mockClient,
            workSource: CreateWorkSourceOptions(
                organizationUrl: "https://dev.azure.com/testorg",
                project: "TestProject",
                activeState: "resolved",
                completedState: "Resolved",
                completedStates: ["Closed"]));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => validator.StartAsync(CancellationToken.None));

        Assert.Contains("ActiveState and CompletedState must be distinct", ex.Message);
    }

    [Fact]
    public async Task StartAsync_EmptyValidStatesFromClient_SkipsValidation()
    {
        // When the client returns an empty list (e.g., API failure),
        // the validator should skip without throwing.
        var mockClient = new FakeAzureDevOpsBoardsClient
        {
            ValidStates = [],
        };

        var validator = CreateValidatorWithMockClient(
            mockClient,
            workSource: CreateWorkSourceOptions(
                organizationUrl: "https://dev.azure.com/testorg",
                project: "TestProject",
                activeState: "Active",
                completedState: "Resolved",
                completedStates: ["Resolved"]));

        // Should not throw — empty valid states means skip
        await validator.StartAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartAsync_NullActiveState_SkipsActiveStateCheck()
    {
        // When ActiveState is null/empty, it should not be validated.
        var mockClient = new FakeAzureDevOpsBoardsClient
        {
            ValidStates = ["New", "Resolved"],
        };

        var validator = CreateValidatorWithMockClient(
            mockClient,
            workSource: CreateWorkSourceOptions(
                organizationUrl: "https://dev.azure.com/testorg",
                project: "TestProject",
                activeState: null,
                completedState: "Resolved",
                completedStates: ["Resolved"]));

        await validator.StartAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartAsync_NullCompletedState_SkipsCompletedStateCheck()
    {
        var mockClient = new FakeAzureDevOpsBoardsClient
        {
            ValidStates = ["New", "InProgress"],
        };

        var validator = CreateValidatorWithMockClient(
            mockClient,
            workSource: CreateWorkSourceOptions(
                organizationUrl: "https://dev.azure.com/testorg",
                project: "TestProject",
                activeState: "InProgress",
                completedState: null,
                completedStates: ["InProgress"]));

        await validator.StartAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartAsync_EmptyCompletedStates_SkipsCompletedStatesCheck()
    {
        var mockClient = new FakeAzureDevOpsBoardsClient
        {
            ValidStates = ["New", "Resolved"],
        };

        var validator = CreateValidatorWithMockClient(
            mockClient,
            workSource: CreateWorkSourceOptions(
                organizationUrl: "https://dev.azure.com/testorg",
                project: "TestProject",
                activeState: "New",
                completedState: "Resolved",
                completedStates: [])); // empty list

        await validator.StartAsync(CancellationToken.None);
    }

    // ═══════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════

    private static IOptions<WorkSourceOptions> CreateWorkSourceOptions(
        string provider = "AzureDevOpsBoards",
        string? organizationUrl = null,
        string? project = null,
        string? activeState = "Active",
        string? completedState = "Resolved",
        IReadOnlyList<string>? completedStates = null)
    {
        return global::Microsoft.Extensions.Options.Options.Create(new WorkSourceOptions
        {
            Provider = provider,
            OrganizationUrl = organizationUrl,
            Project = project,
            ActiveState = activeState,
            CompletedState = completedState,
            CompletedStates = completedStates ?? ["Resolved"],
        });
    }

    private static IOptions<AzureDevOpsBoardsOptions> CreateBoardsOptions(string? pat = null)
    {
        return global::Microsoft.Extensions.Options.Options.Create(new AzureDevOpsBoardsOptions
        {
            PersonalAccessToken = pat ?? string.Empty,
        });
    }

    /// <summary>
    /// Create a validator for skip-path tests that use a no-op scope factory.
    /// The scope factory is never called because skip paths return early.
    /// </summary>
    private static AzureDevOpsBoardStateStartupValidator CreateValidator(
        IOptions<WorkSourceOptions>? workSource = null,
        IOptions<AzureDevOpsBoardsOptions>? boards = null)
    {
        return new AzureDevOpsBoardStateStartupValidator(
            workSource ?? CreateWorkSourceOptions(),
            boards ?? CreateBoardsOptions(),
            new NoOpScopeFactory(),
            NullLogger<AzureDevOpsBoardStateStartupValidator>.Instance);
    }

    /// <summary>
    /// Create a validator backed by a DI container with a mocked <see cref="IAzureDevOpsBoardsClient"/>.
    /// </summary>
    private static AzureDevOpsBoardStateStartupValidator CreateValidatorWithMockClient(
        IAzureDevOpsBoardsClient mockClient,
        IOptions<WorkSourceOptions> workSource,
        IOptions<AzureDevOpsBoardsOptions>? boards = null)
    {
        var boardsOptions = boards ?? CreateBoardsOptions(pat: "test-pat");

        var services = new ServiceCollection();
        services.AddScoped<IAzureDevOpsBoardsClient>(_ => mockClient);
        services.AddSingleton(workSource);
        services.AddSingleton(boardsOptions);

        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        return new AzureDevOpsBoardStateStartupValidator(
            workSource,
            boardsOptions,
            scopeFactory,
            NullLogger<AzureDevOpsBoardStateStartupValidator>.Instance);
    }

    // ── Test infrastructure ────────────────────────────────────────

    /// <summary>
    /// No-op scope factory for tests where the scope is never created (skip paths).
    /// </summary>
    private sealed class NoOpScopeFactory : IServiceScopeFactory
    {
        public IServiceScope CreateScope() =>
            throw new NotSupportedException("Scope should not be created in skip-path tests.");
    }

    /// <summary>
    /// Minimal fake implementation of <see cref="IAzureDevOpsBoardsClient"/>
    /// for unit testing the startup validator.
    /// </summary>
    private sealed class FakeAzureDevOpsBoardsClient : IAzureDevOpsBoardsClient
    {
        public IReadOnlyList<string> ValidStates { get; init; } = [];

        public Task<IReadOnlyList<WorkCandidate>> QueryWorkItemsAsync(BoardsQueryParameters parameters, CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task<ClaimResult> TryClaimWorkItemAsync(ExternalWorkRef workRef, ClaimRequest request, CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task<bool> UpdateWorkItemStatusAsync(ExternalWorkRef workRef, ExternalWorkStatus status, CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task AddCommentAsync(ExternalWorkRef workRef, string comment, CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task<IReadOnlyList<RepositoryInfo>> ListRepositoriesAsync(string project, CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task<AzureDevOpsConnectivityResult> VerifyConnectivityAsync(string organizationUrl, string project, string personalAccessToken, CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task<IReadOnlyList<WorkItemComment>> GetCommentsAsync(ExternalWorkRef workRef, int maxComments, CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task ReleaseClaimWorkItemAsync(ReleaseClaimRequest request, CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task<IReadOnlyList<string>> GetValidStatesAsync(string project, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>(ValidStates);
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

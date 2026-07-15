using System.Collections.Generic;
using System.Net;
using System.Text.Json;
using AgentController.Application;
using AgentController.Application.Abstractions;
using AgentController.Application.Commands;
using AgentController.Application.Queries;
using AgentController.Application.Results;
using AgentController.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentController.Application.Tests;

public sealed class WorkSourceEnvironmentHandlerTests
{
    private static readonly string[] TestBoardStates = ["New", "Active", "Resolved", "Closed"];
    private static readonly string[] EmptyStates = [];

    [Fact]
    public async Task ListAndGet_ReturnStoredProfilesAndNormalizeLookupKeyWithoutResolvingPat()
    {
        const string environmentVariable = "ADO_HANDLER_TEST_PAT";
        const string rawPat = "raw-pat-that-must-not-be-returned";
        var environments = new FakeWorkSourceEnvironmentStore(
            CreateProfile("zulu"),
            CreateProfile("ado-primary") with
            {
                PatEnvironmentVariable = environmentVariable,
            }
        );
        var listHandler = new ListWorkSourceEnvironmentsQueryHandler(environments);
        var getHandler = new GetWorkSourceEnvironmentByKeyQueryHandler(environments);
        Environment.SetEnvironmentVariable(environmentVariable, rawPat);

        try
        {
            var listed = await listHandler.ExecuteAsync(
                new ListWorkSourceEnvironmentsQuery(),
                CancellationToken.None
            );
            var read = await getHandler.ExecuteAsync(
                new GetWorkSourceEnvironmentByKeyQuery("  ADO-PRIMARY  "),
                CancellationToken.None
            );

            Assert.Equal(["ado-primary", "zulu"], listed.Select(profile => profile.Key));
            Assert.Equal(WorkSourceEnvironmentOperationStatus.Succeeded, read.Status);
            var profile = Assert.IsType<WorkSourceEnvironmentProfile>(read.Environment);
            Assert.Equal(environmentVariable, profile.PatEnvironmentVariable);
            Assert.Equal("ado-primary", environments.LastReadKey);
            Assert.DoesNotContain(
                rawPat,
                JsonSerializer.Serialize(listed),
                StringComparison.Ordinal
            );
            Assert.DoesNotContain(rawPat, JsonSerializer.Serialize(read), StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(environmentVariable, null);
        }
    }

    [Fact]
    public async Task Get_ReturnsTypedValidationAndNotFoundOutcomes()
    {
        var environments = new FakeWorkSourceEnvironmentStore();
        var handler = new GetWorkSourceEnvironmentByKeyQueryHandler(environments);

        var invalid = await handler.ExecuteAsync(
            new GetWorkSourceEnvironmentByKeyQuery("not valid"),
            CancellationToken.None
        );
        var missing = await handler.ExecuteAsync(
            new GetWorkSourceEnvironmentByKeyQuery("missing"),
            CancellationToken.None
        );

        Assert.Equal(WorkSourceEnvironmentOperationStatus.ValidationFailed, invalid.Status);
        Assert.Contains("key", invalid.ValidationErrors.Keys);
        Assert.Equal(WorkSourceEnvironmentOperationStatus.NotFound, missing.Status);
        Assert.Empty(missing.ValidationErrors);
    }

    [Fact]
    public async Task Create_NormalizesProfileAndSetsManagedTimestamps()
    {
        var environments = new FakeWorkSourceEnvironmentStore();
        var handler = new CreateWorkSourceEnvironmentCommandHandler(environments);
        var suppliedTimestamp = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var before = DateTimeOffset.UtcNow;
        var profile = CreateProfile("  ADO.Primary  ") with
        {
            DisplayName = "  Primary Boards  ",
            OrganizationUrl = "  https://dev.azure.com/example/  ",
            Project = "  Agent Controller  ",
            Provider = "  AzureDevOpsBoards  ",
            TagPrefix = "  agent  ",
            CompletedStates = [" Resolved ", "RESOLVED", " Removed "],
            ActiveState = " Active ",
            CompletedState = " Resolved ",
            PatEnvironmentVariable = " ADO_PRIMARY_PAT ",
            CreatedAt = suppliedTimestamp,
            UpdatedAt = suppliedTimestamp,
        };

        var result = await handler.HandleAsync(
            new CreateWorkSourceEnvironmentCommand(profile),
            CancellationToken.None
        );
        var after = DateTimeOffset.UtcNow;

        Assert.Equal(WorkSourceEnvironmentOperationStatus.Succeeded, result.Status);
        var persisted = Assert.IsType<WorkSourceEnvironmentProfile>(environments.LastCreated);
        Assert.Same(persisted, result.Environment);
        Assert.Equal("ado.primary", persisted.Key);
        Assert.Equal("Primary Boards", persisted.DisplayName);
        Assert.Equal("https://dev.azure.com/example", persisted.OrganizationUrl);
        Assert.Equal("Agent Controller", persisted.Project);
        Assert.Equal("AzureDevOpsBoards", persisted.Provider);
        Assert.Equal("agent", persisted.TagPrefix);
        Assert.Equal(["Resolved", "Removed"], persisted.CompletedStates);
        Assert.Equal("Active", persisted.ActiveState);
        Assert.Equal("Resolved", persisted.CompletedState);
        Assert.Equal("ADO_PRIMARY_PAT", persisted.PatEnvironmentVariable);
        Assert.Equal(persisted.CreatedAt, persisted.UpdatedAt);
        Assert.InRange(persisted.CreatedAt, before, after);
    }

    [Fact]
    public async Task Create_RejectsInvalidConnectionBoardAndCredentialSettings()
    {
        var environments = new FakeWorkSourceEnvironmentStore();
        var handler = new CreateWorkSourceEnvironmentCommandHandler(environments);
        var profile = CreateProfile("not valid") with
        {
            DisplayName = " ",
            OrganizationUrl = "ftp://user:secret@example.test/org?token=secret",
            Project = " ",
            TagPrefix = "  ",
            CompletedStates = [" "],
            ActiveState = "Active",
            CompletedState = "active",
            PatEnvironmentVariable = "ENV:raw-pat-value",
        };

        var result = await handler.HandleAsync(
            new CreateWorkSourceEnvironmentCommand(profile),
            CancellationToken.None
        );

        Assert.Equal(WorkSourceEnvironmentOperationStatus.ValidationFailed, result.Status);
        Assert.Contains("key", result.ValidationErrors.Keys);
        Assert.Contains("displayName", result.ValidationErrors.Keys);
        Assert.Contains("organizationUrl", result.ValidationErrors.Keys);
        Assert.Contains("project", result.ValidationErrors.Keys);
        Assert.Contains("completedState", result.ValidationErrors.Keys);
        Assert.Contains("patEnvironmentVariable", result.ValidationErrors.Keys);
        Assert.Null(environments.LastCreated);
    }

    [Fact]
    public async Task Create_RejectsMissingProfileAndDuplicateNormalizedKey()
    {
        var environments = new FakeWorkSourceEnvironmentStore(CreateProfile("shared"));
        var handler = new CreateWorkSourceEnvironmentCommandHandler(environments);

        var missing = await handler.HandleAsync(
            new CreateWorkSourceEnvironmentCommand(null!),
            CancellationToken.None
        );
        var duplicate = await handler.HandleAsync(
            new CreateWorkSourceEnvironmentCommand(CreateProfile(" SHARED ")),
            CancellationToken.None
        );

        Assert.Equal(WorkSourceEnvironmentOperationStatus.ValidationFailed, missing.Status);
        Assert.Contains("profile", missing.ValidationErrors.Keys);
        Assert.Equal(WorkSourceEnvironmentOperationStatus.Conflict, duplicate.Status);
        Assert.Contains(
            "shared",
            Assert.IsType<string>(duplicate.Detail),
            StringComparison.Ordinal
        );
        Assert.Equal("shared", environments.LastCreated?.Key);
    }

    [Fact]
    public async Task Update_NormalizesMutableFieldsAndPreservesCreationTimestamp()
    {
        var createdAt = new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);
        var original = CreateProfile("production") with
        {
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
        };
        var environments = new FakeWorkSourceEnvironmentStore(original);
        var handler = new UpdateWorkSourceEnvironmentCommandHandler(environments);
        var suppliedTimestamp = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var update = CreateProfile(" PRODUCTION ") with
        {
            DisplayName = " Updated Boards ",
            Enabled = false,
            Project = " Updated Project ",
            TagPrefix = " custom ",
            CompletedStates = [" Done ", "DONE"],
            PatEnvironmentVariable = " ADO_UPDATED_PAT ",
            CreatedAt = suppliedTimestamp,
            UpdatedAt = suppliedTimestamp,
        };
        var before = DateTimeOffset.UtcNow;

        var result = await handler.HandleAsync(
            new UpdateWorkSourceEnvironmentCommand(" production ", update),
            CancellationToken.None
        );
        var after = DateTimeOffset.UtcNow;

        Assert.Equal(WorkSourceEnvironmentOperationStatus.Succeeded, result.Status);
        var persisted = Assert.IsType<WorkSourceEnvironmentProfile>(environments.LastUpdated);
        Assert.Equal("production", persisted.Key);
        Assert.Equal("Updated Boards", persisted.DisplayName);
        Assert.False(persisted.Enabled);
        Assert.Equal("Updated Project", persisted.Project);
        Assert.Equal("custom", persisted.TagPrefix);
        Assert.Equal(["Done"], persisted.CompletedStates);
        Assert.Equal("ADO_UPDATED_PAT", persisted.PatEnvironmentVariable);
        Assert.Equal(createdAt, persisted.CreatedAt);
        Assert.InRange(persisted.UpdatedAt, before, after);
        Assert.Same(persisted, result.Environment);
    }

    [Fact]
    public async Task Update_RejectsImmutableKeyChangesAndReturnsNotFoundForMissingProfile()
    {
        var environments = new FakeWorkSourceEnvironmentStore(CreateProfile("original"));
        var handler = new UpdateWorkSourceEnvironmentCommandHandler(environments);

        var changedKey = await handler.HandleAsync(
            new UpdateWorkSourceEnvironmentCommand("original", CreateProfile("replacement")),
            CancellationToken.None
        );
        var missing = await handler.HandleAsync(
            new UpdateWorkSourceEnvironmentCommand("missing", CreateProfile("missing")),
            CancellationToken.None
        );

        Assert.Equal(WorkSourceEnvironmentOperationStatus.ValidationFailed, changedKey.Status);
        Assert.Contains(
            "immutable",
            changedKey.ValidationErrors["key"].Single(),
            StringComparison.OrdinalIgnoreCase
        );
        Assert.Equal(WorkSourceEnvironmentOperationStatus.NotFound, missing.Status);
        Assert.Null(environments.LastUpdated);
    }

    [Fact]
    public async Task Delete_ReturnsConflictWhileARepositoryReferencesEnvironment()
    {
        var environments = new FakeWorkSourceEnvironmentStore(CreateProfile("shared"));
        var repositories = new FakeRepositoryStore(
            new RepositoryProfile { Key = "service-b", AzureDevOpsEnvironmentKey = " SHARED " },
            new RepositoryProfile { Key = "service-a", AzureDevOpsEnvironmentKey = "shared" }
        );
        var handler = new DeleteWorkSourceEnvironmentCommandHandler(environments, repositories);

        var result = await handler.HandleAsync(
            new DeleteWorkSourceEnvironmentCommand(" SHARED "),
            CancellationToken.None
        );

        Assert.Equal(WorkSourceEnvironmentOperationStatus.Conflict, result.Status);
        Assert.Contains(
            "service-a",
            Assert.IsType<string>(result.Detail),
            StringComparison.Ordinal
        );
        Assert.Null(environments.LastDeletedKey);
        Assert.NotNull(await environments.GetByKeyAsync("shared", CancellationToken.None));
    }

    [Fact]
    public async Task Delete_RemovesUnreferencedEnvironmentAndReturnsTypedMissingOutcome()
    {
        var environments = new FakeWorkSourceEnvironmentStore(CreateProfile("temporary"));
        var handler = new DeleteWorkSourceEnvironmentCommandHandler(
            environments,
            new FakeRepositoryStore()
        );

        var deleted = await handler.HandleAsync(
            new DeleteWorkSourceEnvironmentCommand(" TEMPORARY "),
            CancellationToken.None
        );
        var missing = await handler.HandleAsync(
            new DeleteWorkSourceEnvironmentCommand("temporary"),
            CancellationToken.None
        );
        var invalid = await handler.HandleAsync(
            new DeleteWorkSourceEnvironmentCommand("bad key"),
            CancellationToken.None
        );

        Assert.Equal(WorkSourceEnvironmentOperationStatus.Succeeded, deleted.Status);
        Assert.Equal("temporary", environments.LastDeletedKey);
        Assert.Equal(WorkSourceEnvironmentOperationStatus.NotFound, missing.Status);
        Assert.Equal(WorkSourceEnvironmentOperationStatus.ValidationFailed, invalid.Status);
    }

    [Fact]
    public void AddApplicationHandlers_RegistersWorkSourceEnvironmentCommandsAndQueries()
    {
        var services = new ServiceCollection();

        services.AddApplicationHandlers();

        AssertRegistration<
            ICommandHandler<
                CreateWorkSourceEnvironmentCommand,
                WorkSourceEnvironmentOperationResult
            >,
            CreateWorkSourceEnvironmentCommandHandler
        >(services);
        AssertRegistration<
            ICommandHandler<
                UpdateWorkSourceEnvironmentCommand,
                WorkSourceEnvironmentOperationResult
            >,
            UpdateWorkSourceEnvironmentCommandHandler
        >(services);
        AssertRegistration<
            ICommandHandler<
                DeleteWorkSourceEnvironmentCommand,
                WorkSourceEnvironmentOperationResult
            >,
            DeleteWorkSourceEnvironmentCommandHandler
        >(services);
        AssertRegistration<
            IQueryHandler<
                ListWorkSourceEnvironmentsQuery,
                IReadOnlyList<WorkSourceEnvironmentProfile>
            >,
            ListWorkSourceEnvironmentsQueryHandler
        >(services);
        AssertRegistration<
            IQueryHandler<
                GetWorkSourceEnvironmentByKeyQuery,
                WorkSourceEnvironmentOperationResult
            >,
            GetWorkSourceEnvironmentByKeyQueryHandler
        >(services);
        AssertRegistration<
            IQueryHandler<Queries.GetBoardStatesQuery, Results.BoardStatesResult>,
            Queries.GetBoardStatesQueryHandler
        >(services);
    }

    [Fact]
    public async Task GetBoardStates_ReturnsStatesForAzureDevOpsBoardsProvider()
    {
        // Set the PAT env var so the handler passes the PAT check.
        const string envName = "ADO_TEST_PAT";
        var originalPat = Environment.GetEnvironmentVariable(envName);
        try
        {
            Environment.SetEnvironmentVariable(envName, "test-pat-value");

            var profile = CreateProfile("ado-dev");
            var store = new FakeWorkSourceEnvironmentStore(profile);
            var factory = new FakeBoardsClientFactory(TestBoardStates);
            var handler = new Queries.GetBoardStatesQueryHandler(
                store,
                factory,
                NullLogger<Queries.GetBoardStatesQueryHandler>.Instance
            );

            var result = await handler.ExecuteAsync(
                new Queries.GetBoardStatesQuery("  ADO-DEV  "),
                CancellationToken.None
            );

            Assert.Equal(Results.BoardStatesStatus.Succeeded, result.Status);
            // States are returned grouped by work item type.
            Assert.Single(result.StatesByType);
            Assert.True(result.StatesByType.ContainsKey("Default"));
            Assert.Equal(["New", "Active", "Resolved", "Closed"], result.StatesByType["Default"]);
            Assert.Null(result.Error);
            Assert.Equal("ado-dev", store.LastReadKey);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, originalPat);
        }
    }

    [Fact]
    public async Task GetBoardStates_ReturnsNotFoundForMissingEnvironment()
    {
        var store = new FakeWorkSourceEnvironmentStore();
        var factory = new FakeBoardsClientFactory(EmptyStates);
        var handler = new Queries.GetBoardStatesQueryHandler(
            store,
            factory,
            NullLogger<Queries.GetBoardStatesQueryHandler>.Instance
        );

        var result = await handler.ExecuteAsync(
            new Queries.GetBoardStatesQuery("missing"),
            CancellationToken.None
        );

        Assert.Equal(Results.BoardStatesStatus.NotFound, result.Status);
        Assert.Empty(result.StatesByType);
        Assert.Contains("not found", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetBoardStates_ReturnsNotFoundForInvalidKey()
    {
        var store = new FakeWorkSourceEnvironmentStore();
        var factory = new FakeBoardsClientFactory(EmptyStates);
        var handler = new Queries.GetBoardStatesQueryHandler(
            store,
            factory,
            NullLogger<Queries.GetBoardStatesQueryHandler>.Instance
        );

        var result = await handler.ExecuteAsync(
            new Queries.GetBoardStatesQuery("not valid"),
            CancellationToken.None
        );

        Assert.Equal(Results.BoardStatesStatus.NotFound, result.Status);
        Assert.Empty(result.StatesByType);
        Assert.Contains("Invalid", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetBoardStates_ReturnsUnsupportedProviderForNonAdoEnvironment()
    {
        var profile = CreateProfile("other") with { Provider = "GitHub" };
        var store = new FakeWorkSourceEnvironmentStore(profile);
        var factory = new FakeBoardsClientFactory(EmptyStates);
        var handler = new Queries.GetBoardStatesQueryHandler(
            store,
            factory,
            NullLogger<Queries.GetBoardStatesQueryHandler>.Instance
        );

        var result = await handler.ExecuteAsync(
            new Queries.GetBoardStatesQuery("other"),
            CancellationToken.None
        );

        Assert.Equal(Results.BoardStatesStatus.UnsupportedProvider, result.Status);
        Assert.Empty(result.StatesByType);
        Assert.Contains("GitHub", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetBoardStates_ReturnsConnectivityErrorWhenNoStatesDiscovered()
    {
        // Set the PAT env var so the handler passes the PAT check.
        const string envName = "ADO_TEST_PAT";
        var originalPat = Environment.GetEnvironmentVariable(envName);
        try
        {
            Environment.SetEnvironmentVariable(envName, "test-pat-value");

            var profile = CreateProfile("ado-dev");
            var store = new FakeWorkSourceEnvironmentStore(profile);
            var factory = new FakeBoardsClientFactory(EmptyStates); // empty = no WITs with states
            var handler = new Queries.GetBoardStatesQueryHandler(
                store,
                factory,
                NullLogger<Queries.GetBoardStatesQueryHandler>.Instance
            );

            var result = await handler.ExecuteAsync(
                new Queries.GetBoardStatesQuery("ado-dev"),
                CancellationToken.None
            );

            Assert.Equal(Results.BoardStatesStatus.ConnectivityError, result.Status);
            Assert.Empty(result.StatesByType);
            Assert.NotNull(result.Error);
            Assert.Contains("Unable to retrieve board states", result.Error);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, originalPat);
        }
    }

    [Fact]
    public async Task GetBoardStates_ReturnsConnectivityErrorWhenDisabled()
    {
        var profile = CreateProfile("ado-dev") with { Enabled = false };
        var store = new FakeWorkSourceEnvironmentStore(profile);
        var factory = new FakeBoardsClientFactory(TestBoardStates);
        var handler = new Queries.GetBoardStatesQueryHandler(
            store,
            factory,
            NullLogger<Queries.GetBoardStatesQueryHandler>.Instance
        );

        var result = await handler.ExecuteAsync(
            new Queries.GetBoardStatesQuery("ado-dev"),
            CancellationToken.None
        );

        Assert.Equal(Results.BoardStatesStatus.ConnectivityError, result.Status);
        Assert.Empty(result.StatesByType);
        Assert.NotNull(result.Error);
        Assert.Contains("disabled", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetBoardStates_ReturnsConnectivityErrorWhenPatEnvVarMissing()
    {
        // Profile references an env var that does not exist in the test process.
        var profile = CreateProfile("ado-dev")
            with { PatEnvironmentVariable = "__NONEXISTENT_TEST_PAT_VAR__" };
        var store = new FakeWorkSourceEnvironmentStore(profile);
        var factory = new FakeBoardsClientFactory(TestBoardStates);
        var handler = new Queries.GetBoardStatesQueryHandler(
            store,
            factory,
            NullLogger<Queries.GetBoardStatesQueryHandler>.Instance
        );

        var result = await handler.ExecuteAsync(
            new Queries.GetBoardStatesQuery("ado-dev"),
            CancellationToken.None
        );

        Assert.Equal(Results.BoardStatesStatus.ConnectivityError, result.Status);
        Assert.Empty(result.StatesByType);
        Assert.NotNull(result.Error);
        Assert.Contains("__NONEXISTENT_TEST_PAT_VAR__", result.Error);
        Assert.Contains("not set", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetBoardStates_ReturnsConnectivityErrorWithHttpErrorDetail()
    {
        // Set the PAT env var so the handler passes the PAT check.
        const string envName = "ADO_TEST_PAT";
        var originalPat = Environment.GetEnvironmentVariable(envName);
        try
        {
            Environment.SetEnvironmentVariable(envName, "test-pat-value");

            var profile = CreateProfile("ado-dev");
            var store = new FakeWorkSourceEnvironmentStore(profile);
            var factory = new FakeBoardsClientFactory_Throws(
                new HttpRequestException("Request failed: HTTP 401 Unauthorized")
            );
            var handler = new Queries.GetBoardStatesQueryHandler(
                store,
                factory,
                NullLogger<Queries.GetBoardStatesQueryHandler>.Instance
            );

            var result = await handler.ExecuteAsync(
                new Queries.GetBoardStatesQuery("ado-dev"),
                CancellationToken.None
            );

            Assert.Equal(Results.BoardStatesStatus.ConnectivityError, result.Status);
            Assert.Empty(result.StatesByType);
            Assert.NotNull(result.Error);
            Assert.Contains("Azure DevOps request failed", result.Error);
            Assert.Contains("401", result.Error);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, originalPat);
        }
    }

    private static WorkSourceEnvironmentProfile CreateProfile(string key) =>
        new()
        {
            Key = key,
            DisplayName = $"{key} Boards",
            Enabled = true,
            Provider = "AzureDevOpsBoards",
            TagPrefix = "agent",
            OrganizationUrl = "https://dev.azure.com/example",
            Project = "Agent Controller",
            CompletedStates = ["Removed"],
            ActiveState = "Active",
            CompletedState = "Resolved",
            PatEnvironmentVariable = "ADO_TEST_PAT",
        };

    private static void AssertRegistration<TService, TImplementation>(IServiceCollection services)
    {
        Assert.Contains(
            services,
            descriptor =>
                descriptor.ServiceType == typeof(TService)
                && descriptor.ImplementationType == typeof(TImplementation)
                && descriptor.Lifetime == ServiceLifetime.Scoped
        );
    }

    private sealed class FakeWorkSourceEnvironmentStore : IWorkSourceEnvironmentStore
    {
        private readonly Dictionary<string, WorkSourceEnvironmentProfile> _profiles;

        public FakeWorkSourceEnvironmentStore(params WorkSourceEnvironmentProfile[] profiles)
        {
            _profiles = profiles.ToDictionary(profile => profile.Key, StringComparer.Ordinal);
        }

        public string? LastReadKey { get; private set; }

        public WorkSourceEnvironmentProfile? LastCreated { get; private set; }

        public WorkSourceEnvironmentProfile? LastUpdated { get; private set; }

        public string? LastDeletedKey { get; private set; }

        public Task<IReadOnlyList<WorkSourceEnvironmentProfile>> ListAsync(
            CancellationToken cancellationToken
        )
        {
            IReadOnlyList<WorkSourceEnvironmentProfile> profiles = _profiles
                .Values.OrderBy(profile => profile.Key, StringComparer.Ordinal)
                .ToList();
            return Task.FromResult(profiles);
        }

        public Task<WorkSourceEnvironmentProfile?> GetByKeyAsync(
            string key,
            CancellationToken cancellationToken
        )
        {
            LastReadKey = key;
            _profiles.TryGetValue(key, out var profile);
            return Task.FromResult(profile);
        }

        public Task<bool> CreateAsync(
            WorkSourceEnvironmentProfile profile,
            CancellationToken cancellationToken
        )
        {
            LastCreated = profile;
            return Task.FromResult(_profiles.TryAdd(profile.Key, profile));
        }

        public Task<bool> UpdateAsync(
            WorkSourceEnvironmentProfile profile,
            CancellationToken cancellationToken
        )
        {
            if (!_profiles.ContainsKey(profile.Key))
            {
                return Task.FromResult(false);
            }

            LastUpdated = profile;
            _profiles[profile.Key] = profile;
            return Task.FromResult(true);
        }

        public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken)
        {
            LastDeletedKey = key;
            return Task.FromResult(_profiles.Remove(key));
        }
    }

    private sealed class FakeRepositoryStore(params RepositoryProfile[] profiles) : IRepositoryStore
    {
        private readonly IReadOnlyList<RepositoryProfile> _profiles = profiles;

        public Task<IReadOnlyList<RepositoryProfile>> ListAsync(
            CancellationToken cancellationToken
        ) => Task.FromResult(_profiles);

        public Task<RepositoryProfile?> GetByKeyAsync(
            string key,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<bool> CreateAsync(
            RepositoryProfile profile,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<bool> UpdateAsync(
            RepositoryProfile profile,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task UpsertAsync(RepositoryProfile profile, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeBoardsClientFactory(IReadOnlyList<string> states)
        : IAzureDevOpsBoardsClientFactory
    {
        // Convert flat states to grouped shape (single WIT group) for interface compatibility.
        private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _grouped =
            states.Count > 0
                ? new Dictionary<string, IReadOnlyList<string>> { ["Default"] = states }
                : new Dictionary<string, IReadOnlyList<string>>();

        public IAzureDevOpsBoardsClient Create(WorkSourceEnvironmentProfile profile)
        {
            return new FakeBoardsClient(_grouped);
        }
    }

    private sealed class FakeBoardsClient(IReadOnlyDictionary<string, IReadOnlyList<string>> groupedStates)
        : IAzureDevOpsBoardsClient
    {
        private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _groupedStates = groupedStates;

        public Task<IReadOnlyList<WorkCandidate>> QueryWorkItemsAsync(
            BoardsQueryParameters parameters, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<ClaimResult> TryClaimWorkItemAsync(
            ExternalWorkRef workRef, ClaimRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<bool> UpdateWorkItemStatusAsync(
            ExternalWorkRef workRef, ExternalWorkStatus status, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task AddCommentAsync(
            ExternalWorkRef workRef, string comment, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<RepositoryInfo>> ListRepositoriesAsync(
            string project, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<AzureDevOpsConnectivityResult> VerifyConnectivityAsync(
            string organizationUrl, string project, string personalAccessToken,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<WorkItemComment>> GetCommentsAsync(
            ExternalWorkRef workRef, int maxComments, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task ReleaseClaimWorkItemAsync(
            ReleaseClaimRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> GetValidStatesAsync(
            string project, CancellationToken cancellationToken)
            => Task.FromResult(_groupedStates);
    }

    /// <summary>Fake factory whose client throws on GetValidStatesAsync.</summary>
    private sealed class FakeBoardsClientFactory_Throws(Exception exception)
        : IAzureDevOpsBoardsClientFactory
    {
        public IAzureDevOpsBoardsClient Create(WorkSourceEnvironmentProfile profile)
        {
            return new FakeBoardsClient_Throws(exception);
        }
    }

    private sealed class FakeBoardsClient_Throws(Exception exception)
        : IAzureDevOpsBoardsClient
    {
        private readonly Exception _exception = exception;

        public Task<IReadOnlyList<WorkCandidate>> QueryWorkItemsAsync(
            BoardsQueryParameters parameters, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<ClaimResult> TryClaimWorkItemAsync(
            ExternalWorkRef workRef, ClaimRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<bool> UpdateWorkItemStatusAsync(
            ExternalWorkRef workRef, ExternalWorkStatus status, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task AddCommentAsync(
            ExternalWorkRef workRef, string comment, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<RepositoryInfo>> ListRepositoriesAsync(
            string project, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<AzureDevOpsConnectivityResult> VerifyConnectivityAsync(
            string organizationUrl, string project, string personalAccessToken,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<WorkItemComment>> GetCommentsAsync(
            ExternalWorkRef workRef, int maxComments, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task ReleaseClaimWorkItemAsync(
            ReleaseClaimRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> GetValidStatesAsync(
            string project, CancellationToken cancellationToken)
        {
            throw _exception;
        }
    }
}

using System.Text.Json;
using AgentController.Application.Abstractions;
using AgentController.Application.Commands;
using AgentController.Application.Queries;
using AgentController.Application.Results;
using AgentController.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace AgentController.Application.Tests;

public sealed class AzureDevOpsEnvironmentHandlerTests
{
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
        var listHandler = new ListAzureDevOpsEnvironmentsQueryHandler(environments);
        var getHandler = new GetAzureDevOpsEnvironmentByKeyQueryHandler(environments);
        Environment.SetEnvironmentVariable(environmentVariable, rawPat);

        try
        {
            var listed = await listHandler.ExecuteAsync(
                new ListAzureDevOpsEnvironmentsQuery(),
                CancellationToken.None
            );
            var read = await getHandler.ExecuteAsync(
                new GetAzureDevOpsEnvironmentByKeyQuery("  ADO-PRIMARY  "),
                CancellationToken.None
            );

            Assert.Equal(["ado-primary", "zulu"], listed.Select(profile => profile.Key));
            Assert.Equal(AzureDevOpsEnvironmentOperationStatus.Succeeded, read.Status);
            var profile = Assert.IsType<AzureDevOpsEnvironmentProfile>(read.Environment);
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
        var handler = new GetAzureDevOpsEnvironmentByKeyQueryHandler(environments);

        var invalid = await handler.ExecuteAsync(
            new GetAzureDevOpsEnvironmentByKeyQuery("not valid"),
            CancellationToken.None
        );
        var missing = await handler.ExecuteAsync(
            new GetAzureDevOpsEnvironmentByKeyQuery("missing"),
            CancellationToken.None
        );

        Assert.Equal(AzureDevOpsEnvironmentOperationStatus.ValidationFailed, invalid.Status);
        Assert.Contains("key", invalid.ValidationErrors.Keys);
        Assert.Equal(AzureDevOpsEnvironmentOperationStatus.NotFound, missing.Status);
        Assert.Empty(missing.ValidationErrors);
    }

    [Fact]
    public async Task Create_NormalizesProfileAndSetsManagedTimestamps()
    {
        var environments = new FakeWorkSourceEnvironmentStore();
        var handler = new CreateAzureDevOpsEnvironmentCommandHandler(environments);
        var suppliedTimestamp = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var before = DateTimeOffset.UtcNow;
        var profile = CreateProfile("  ADO.Primary  ") with
        {
            DisplayName = "  Primary Boards  ",
            OrganizationUrl = "  https://dev.azure.com/example/  ",
            Project = "  Agent Controller  ",
            WorkItemType = "  User Story  ",
            EligibleTags = [" ready ", "READY", " agent "],
            ExcludedTags = [" blocked ", "BLOCKED"],
            EligibleStates = [" New ", "NEW"],
            ExcludedStates = [" Removed "],
            ActiveState = " Active ",
            CompletedState = " Resolved ",
            PatEnvironmentVariable = " ADO_PRIMARY_PAT ",
            CreatedAt = suppliedTimestamp,
            UpdatedAt = suppliedTimestamp,
        };

        var result = await handler.HandleAsync(
            new CreateAzureDevOpsEnvironmentCommand(profile),
            CancellationToken.None
        );
        var after = DateTimeOffset.UtcNow;

        Assert.Equal(AzureDevOpsEnvironmentOperationStatus.Succeeded, result.Status);
        var persisted = Assert.IsType<AzureDevOpsEnvironmentProfile>(environments.LastCreated);
        Assert.Same(persisted, result.Environment);
        Assert.Equal("ado.primary", persisted.Key);
        Assert.Equal("Primary Boards", persisted.DisplayName);
        Assert.Equal("https://dev.azure.com/example", persisted.OrganizationUrl);
        Assert.Equal("Agent Controller", persisted.Project);
        Assert.Equal("User Story", persisted.WorkItemType);
        Assert.Equal(["ready", "agent"], persisted.EligibleTags);
        Assert.Equal(["blocked"], persisted.ExcludedTags);
        Assert.Equal(["New"], persisted.EligibleStates);
        Assert.Equal(["Removed"], persisted.ExcludedStates);
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
        var handler = new CreateAzureDevOpsEnvironmentCommandHandler(environments);
        var profile = CreateProfile("not valid") with
        {
            DisplayName = " ",
            OrganizationUrl = "ftp://user:secret@example.test/org?token=secret",
            Project = " ",
            WorkItemType = " ",
            EligibleTags = ["ready", " "],
            ExcludedTags = ["READY"],
            EligibleStates = ["New"],
            ExcludedStates = ["new"],
            ActiveState = "Active",
            CompletedState = "active",
            PatEnvironmentVariable = "ENV:raw-pat-value",
        };

        var result = await handler.HandleAsync(
            new CreateAzureDevOpsEnvironmentCommand(profile),
            CancellationToken.None
        );

        Assert.Equal(AzureDevOpsEnvironmentOperationStatus.ValidationFailed, result.Status);
        Assert.Contains("key", result.ValidationErrors.Keys);
        Assert.Contains("displayName", result.ValidationErrors.Keys);
        Assert.Contains("organizationUrl", result.ValidationErrors.Keys);
        Assert.Contains("project", result.ValidationErrors.Keys);
        Assert.Contains("workItemType", result.ValidationErrors.Keys);
        Assert.Contains("eligibleTags", result.ValidationErrors.Keys);
        Assert.Contains("excludedTags", result.ValidationErrors.Keys);
        Assert.Contains("excludedStates", result.ValidationErrors.Keys);
        Assert.Contains("completedState", result.ValidationErrors.Keys);
        Assert.Contains("patEnvironmentVariable", result.ValidationErrors.Keys);
        Assert.Null(environments.LastCreated);
    }

    [Fact]
    public async Task Create_RejectsMissingProfileAndDuplicateNormalizedKey()
    {
        var environments = new FakeWorkSourceEnvironmentStore(CreateProfile("shared"));
        var handler = new CreateAzureDevOpsEnvironmentCommandHandler(environments);

        var missing = await handler.HandleAsync(
            new CreateAzureDevOpsEnvironmentCommand(null!),
            CancellationToken.None
        );
        var duplicate = await handler.HandleAsync(
            new CreateAzureDevOpsEnvironmentCommand(CreateProfile(" SHARED ")),
            CancellationToken.None
        );

        Assert.Equal(AzureDevOpsEnvironmentOperationStatus.ValidationFailed, missing.Status);
        Assert.Contains("profile", missing.ValidationErrors.Keys);
        Assert.Equal(AzureDevOpsEnvironmentOperationStatus.Conflict, duplicate.Status);
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
        var handler = new UpdateAzureDevOpsEnvironmentCommandHandler(environments);
        var suppliedTimestamp = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var update = CreateProfile(" PRODUCTION ") with
        {
            DisplayName = " Updated Boards ",
            Enabled = false,
            Project = " Updated Project ",
            EligibleTags = [" agent ", "AGENT"],
            PatEnvironmentVariable = " ADO_UPDATED_PAT ",
            CreatedAt = suppliedTimestamp,
            UpdatedAt = suppliedTimestamp,
        };
        var before = DateTimeOffset.UtcNow;

        var result = await handler.HandleAsync(
            new UpdateAzureDevOpsEnvironmentCommand(" production ", update),
            CancellationToken.None
        );
        var after = DateTimeOffset.UtcNow;

        Assert.Equal(AzureDevOpsEnvironmentOperationStatus.Succeeded, result.Status);
        var persisted = Assert.IsType<AzureDevOpsEnvironmentProfile>(environments.LastUpdated);
        Assert.Equal("production", persisted.Key);
        Assert.Equal("Updated Boards", persisted.DisplayName);
        Assert.False(persisted.Enabled);
        Assert.Equal("Updated Project", persisted.Project);
        Assert.Equal(["agent"], persisted.EligibleTags);
        Assert.Equal("ADO_UPDATED_PAT", persisted.PatEnvironmentVariable);
        Assert.Equal(createdAt, persisted.CreatedAt);
        Assert.InRange(persisted.UpdatedAt, before, after);
        Assert.Same(persisted, result.Environment);
    }

    [Fact]
    public async Task Update_RejectsImmutableKeyChangesAndReturnsNotFoundForMissingProfile()
    {
        var environments = new FakeWorkSourceEnvironmentStore(CreateProfile("original"));
        var handler = new UpdateAzureDevOpsEnvironmentCommandHandler(environments);

        var changedKey = await handler.HandleAsync(
            new UpdateAzureDevOpsEnvironmentCommand("original", CreateProfile("replacement")),
            CancellationToken.None
        );
        var missing = await handler.HandleAsync(
            new UpdateAzureDevOpsEnvironmentCommand("missing", CreateProfile("missing")),
            CancellationToken.None
        );

        Assert.Equal(AzureDevOpsEnvironmentOperationStatus.ValidationFailed, changedKey.Status);
        Assert.Contains(
            "immutable",
            changedKey.ValidationErrors["key"].Single(),
            StringComparison.OrdinalIgnoreCase
        );
        Assert.Equal(AzureDevOpsEnvironmentOperationStatus.NotFound, missing.Status);
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
        var handler = new DeleteAzureDevOpsEnvironmentCommandHandler(environments, repositories);

        var result = await handler.HandleAsync(
            new DeleteAzureDevOpsEnvironmentCommand(" SHARED "),
            CancellationToken.None
        );

        Assert.Equal(AzureDevOpsEnvironmentOperationStatus.Conflict, result.Status);
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
        var handler = new DeleteAzureDevOpsEnvironmentCommandHandler(
            environments,
            new FakeRepositoryStore()
        );

        var deleted = await handler.HandleAsync(
            new DeleteAzureDevOpsEnvironmentCommand(" TEMPORARY "),
            CancellationToken.None
        );
        var missing = await handler.HandleAsync(
            new DeleteAzureDevOpsEnvironmentCommand("temporary"),
            CancellationToken.None
        );
        var invalid = await handler.HandleAsync(
            new DeleteAzureDevOpsEnvironmentCommand("bad key"),
            CancellationToken.None
        );

        Assert.Equal(AzureDevOpsEnvironmentOperationStatus.Succeeded, deleted.Status);
        Assert.Equal("temporary", environments.LastDeletedKey);
        Assert.Equal(AzureDevOpsEnvironmentOperationStatus.NotFound, missing.Status);
        Assert.Equal(AzureDevOpsEnvironmentOperationStatus.ValidationFailed, invalid.Status);
    }

    [Fact]
    public void AddApplicationHandlers_RegistersAzureDevOpsEnvironmentCommandsAndQueries()
    {
        var services = new ServiceCollection();

        services.AddApplicationHandlers();

        AssertRegistration<
            ICommandHandler<
                CreateAzureDevOpsEnvironmentCommand,
                AzureDevOpsEnvironmentOperationResult
            >,
            CreateAzureDevOpsEnvironmentCommandHandler
        >(services);
        AssertRegistration<
            ICommandHandler<
                UpdateAzureDevOpsEnvironmentCommand,
                AzureDevOpsEnvironmentOperationResult
            >,
            UpdateAzureDevOpsEnvironmentCommandHandler
        >(services);
        AssertRegistration<
            ICommandHandler<
                DeleteAzureDevOpsEnvironmentCommand,
                AzureDevOpsEnvironmentOperationResult
            >,
            DeleteAzureDevOpsEnvironmentCommandHandler
        >(services);
        AssertRegistration<
            IQueryHandler<
                ListAzureDevOpsEnvironmentsQuery,
                IReadOnlyList<AzureDevOpsEnvironmentProfile>
            >,
            ListAzureDevOpsEnvironmentsQueryHandler
        >(services);
        AssertRegistration<
            IQueryHandler<
                GetAzureDevOpsEnvironmentByKeyQuery,
                AzureDevOpsEnvironmentOperationResult
            >,
            GetAzureDevOpsEnvironmentByKeyQueryHandler
        >(services);
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
}

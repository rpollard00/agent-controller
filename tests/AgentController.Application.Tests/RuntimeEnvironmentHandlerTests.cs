using System.Text.Json;
using AgentController.Application.Abstractions;
using AgentController.Application.Commands;
using AgentController.Application.Queries;
using AgentController.Application.Results;
using AgentController.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace AgentController.Application.Tests;

public sealed class RuntimeEnvironmentHandlerTests
{
    [Fact]
    public async Task ListAndGet_ReturnStoredProfilesAndNormalizeLookupWithoutResolvingVariables()
    {
        const string sourceVariable = "RUNTIME_HANDLER_TEST_PAT";
        const string rawValue = "raw-secret-that-must-not-be-returned";
        var environments = new FakeRuntimeEnvironmentStore(
            CreateProfile("zulu"),
            CreateProfile("local-pi") with
            {
                RuntimeSettings = CreateProfile("local-pi").RuntimeSettings with
                {
                    ForwardEnvironmentVariables = new Dictionary<string, string>
                    {
                        ["AZURE_DEVOPS_EXT_PAT"] = sourceVariable,
                    },
                },
            }
        );
        var listHandler = new ListRuntimeEnvironmentsQueryHandler(environments);
        var getHandler = new GetRuntimeEnvironmentByKeyQueryHandler(environments);
        Environment.SetEnvironmentVariable(sourceVariable, rawValue);

        try
        {
            var listed = await listHandler.ExecuteAsync(
                new ListRuntimeEnvironmentsQuery(),
                CancellationToken.None
            );
            var read = await getHandler.ExecuteAsync(
                new GetRuntimeEnvironmentByKeyQuery("  LOCAL-PI  "),
                CancellationToken.None
            );

            Assert.Equal(["local-pi", "zulu"], listed.Select(profile => profile.Key));
            Assert.Equal(RuntimeEnvironmentOperationStatus.Succeeded, read.Status);
            var profile = Assert.IsType<RuntimeEnvironmentProfile>(read.Environment);
            Assert.Equal(
                sourceVariable,
                profile.RuntimeSettings.ForwardEnvironmentVariables["AZURE_DEVOPS_EXT_PAT"]
            );
            Assert.Equal("local-pi", environments.LastReadKey);
            Assert.DoesNotContain(
                rawValue,
                JsonSerializer.Serialize(listed),
                StringComparison.Ordinal
            );
            Assert.DoesNotContain(
                rawValue,
                JsonSerializer.Serialize(read),
                StringComparison.Ordinal
            );
        }
        finally
        {
            Environment.SetEnvironmentVariable(sourceVariable, null);
        }
    }

    [Fact]
    public async Task Get_ReturnsTypedValidationAndNotFoundOutcomes()
    {
        var environments = new FakeRuntimeEnvironmentStore();
        var handler = new GetRuntimeEnvironmentByKeyQueryHandler(environments);

        var invalid = await handler.ExecuteAsync(
            new GetRuntimeEnvironmentByKeyQuery("not valid"),
            CancellationToken.None
        );
        var missing = await handler.ExecuteAsync(
            new GetRuntimeEnvironmentByKeyQuery("missing"),
            CancellationToken.None
        );

        Assert.Equal(RuntimeEnvironmentOperationStatus.ValidationFailed, invalid.Status);
        Assert.Contains("key", invalid.ValidationErrors.Keys);
        Assert.Equal(RuntimeEnvironmentOperationStatus.NotFound, missing.Status);
        Assert.Empty(missing.ValidationErrors);
    }

    [Fact]
    public async Task Create_NormalizesLoadoutsAndManagedTimestampsAndDropsControllerOwnedSettings()
    {
        var environments = new FakeRuntimeEnvironmentStore();
        var handler = new CreateRuntimeEnvironmentCommandHandler(environments);
        var suppliedTimestamp = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var profile = CreateProfile("  LOCAL.PI  ") with
        {
            DisplayName = "  Local pi-materia  ",
            EnvironmentProvider = " localworkspace ",
            EnvironmentSettings = new EnvironmentProviderSettings
            {
                WorkspaceRoot = "  ~/.agent-controller/runs  ",
            },
            RuntimeProvider = " pimateria ",
            RuntimeSettings = new RuntimeProviderSettings
            {
                PiExecutablePath = "  /usr/local/bin/pi  ",
                ControllerBaseUrl = "  https://controller.example.test/  ",
                PtyWrapperPath = "  script  ",
                PtyWrapperArgs = "  -qfc  ",
                Loadouts = new Dictionary<ExecutionKind, string>
                {
                    [ExecutionKind.Rework] = "  rework  ",
                    [ExecutionKind.NewWork] = "  new-work  ",
                },
                ForwardEnvironmentVariables = new Dictionary<string, string>
                {
                    [" Z_TARGET "] = " Z_SOURCE ",
                    ["A_TARGET"] = " A_SOURCE ",
                },
            },
            CreatedAt = suppliedTimestamp,
            UpdatedAt = suppliedTimestamp,
        };
        var before = DateTimeOffset.UtcNow;

        var result = await handler.HandleAsync(
            new CreateRuntimeEnvironmentCommand(profile),
            CancellationToken.None
        );
        var after = DateTimeOffset.UtcNow;

        Assert.Equal(RuntimeEnvironmentOperationStatus.Succeeded, result.Status);
        var persisted = Assert.IsType<RuntimeEnvironmentProfile>(environments.LastCreated);
        Assert.Same(persisted, result.Environment);
        Assert.Equal("local.pi", persisted.Key);
        Assert.Equal("Local pi-materia", persisted.DisplayName);
        Assert.Equal("LocalWorkspace", persisted.EnvironmentProvider);
        Assert.Equal("~/.agent-controller/runs", persisted.EnvironmentSettings.WorkspaceRoot);
        Assert.Equal("PiMateria", persisted.RuntimeProvider);
        // Controller-owned process settings are accepted for compatibility but dropped:
        // they are not persisted so stale stored overrides cannot alter execution.
        Assert.Null(persisted.RuntimeSettings.PiExecutablePath);
        Assert.Null(persisted.RuntimeSettings.ControllerBaseUrl);
        Assert.Null(persisted.RuntimeSettings.PtyWrapperPath);
        Assert.Null(persisted.RuntimeSettings.PtyWrapperArgs);
        Assert.Empty(persisted.RuntimeSettings.ForwardEnvironmentVariables);
        // Loadouts remain a user-level, profile-specific control and are normalized/persisted.
        Assert.Equal(
            [ExecutionKind.NewWork, ExecutionKind.Rework],
            persisted.RuntimeSettings.Loadouts.Keys
        );
        Assert.Equal("new-work", persisted.RuntimeSettings.Loadouts[ExecutionKind.NewWork]);
        Assert.Equal(persisted.CreatedAt, persisted.UpdatedAt);
        Assert.InRange(persisted.CreatedAt, before, after);
    }

    [Fact]
    public async Task Create_AllowsSupportedMockCombinationWithoutPiSpecificSettings()
    {
        var environments = new FakeRuntimeEnvironmentStore();
        var handler = new CreateRuntimeEnvironmentCommandHandler(environments);
        var profile = CreateMockProfile("local-mock");

        var result = await handler.HandleAsync(
            new CreateRuntimeEnvironmentCommand(profile),
            CancellationToken.None
        );

        Assert.Equal(RuntimeEnvironmentOperationStatus.Succeeded, result.Status);
        var persisted = Assert.IsType<RuntimeEnvironmentProfile>(result.Environment);
        Assert.Equal("LocalWorkspace", persisted.EnvironmentProvider);
        Assert.Equal("MockPiMateria", persisted.RuntimeProvider);
        Assert.Null(persisted.RuntimeSettings.PiExecutablePath);
        Assert.Null(persisted.RuntimeSettings.ControllerBaseUrl);
        Assert.Null(persisted.RuntimeSettings.PtyWrapperPath);
        Assert.Null(persisted.RuntimeSettings.PtyWrapperArgs);
        Assert.Empty(persisted.RuntimeSettings.Loadouts);
        Assert.Empty(persisted.RuntimeSettings.ForwardEnvironmentVariables);
    }

    [Fact]
    public async Task Create_RejectsUnsupportedProvidersAndInvalidLoadoutsButIgnoresControllerOwnedSettings()
    {
        var environments = new FakeRuntimeEnvironmentStore();
        var handler = new CreateRuntimeEnvironmentCommandHandler(environments);
        var profile = CreateProfile("not valid") with
        {
            DisplayName = " ",
            EnvironmentProvider = "ContainerWorkspace",
            EnvironmentSettings = new EnvironmentProviderSettings { WorkspaceRoot = "bad\npath" },
            RuntimeProvider = "PiMateria",
            RuntimeSettings = new RuntimeProviderSettings
            {
                PiExecutablePath = " ",
                ControllerBaseUrl = "ftp://user:secret@example.test/callback?token=secret",
                PtyWrapperPath = "scr\nipt",
                PtyWrapperArgs = new string('x', 4097),
                Loadouts = new Dictionary<ExecutionKind, string>
                {
                    [ExecutionKind.Rework] = " ",
                    [(ExecutionKind)999] = "unknown",
                },
                ForwardEnvironmentVariables = new Dictionary<string, string>
                {
                    ["1_INVALID_TARGET"] = "RAW-secret-value",
                    ["VALID_TARGET"] = "not a variable",
                },
            },
        };

        var result = await handler.HandleAsync(
            new CreateRuntimeEnvironmentCommand(profile),
            CancellationToken.None
        );

        Assert.Equal(RuntimeEnvironmentOperationStatus.ValidationFailed, result.Status);
        Assert.Contains("key", result.ValidationErrors.Keys);
        Assert.Contains("displayName", result.ValidationErrors.Keys);
        Assert.Contains("environmentProvider", result.ValidationErrors.Keys);
        Assert.Contains("environmentSettings.workspaceRoot", result.ValidationErrors.Keys);
        Assert.Contains("runtimeSettings.loadouts", result.ValidationErrors.Keys);
        // Controller-owned process settings are no longer validated per-profile, so invalid
        // executable, controller URL, PTY, and env-var forwarding values are ignored.
        Assert.DoesNotContain("runtimeSettings.piExecutablePath", result.ValidationErrors.Keys);
        Assert.DoesNotContain("runtimeSettings.controllerBaseUrl", result.ValidationErrors.Keys);
        Assert.DoesNotContain("runtimeSettings.ptyWrapperPath", result.ValidationErrors.Keys);
        Assert.DoesNotContain("runtimeSettings.ptyWrapperArgs", result.ValidationErrors.Keys);
        Assert.DoesNotContain(
            "runtimeSettings.forwardEnvironmentVariables",
            result.ValidationErrors.Keys
        );
        Assert.Null(environments.LastCreated);
    }

    [Fact]
    public async Task Create_RejectsMissingSettingsProfileAndDuplicateNormalizedKey()
    {
        var environments = new FakeRuntimeEnvironmentStore(CreateProfile("shared"));
        var handler = new CreateRuntimeEnvironmentCommandHandler(environments);

        var missingProfile = await handler.HandleAsync(
            new CreateRuntimeEnvironmentCommand(null!),
            CancellationToken.None
        );
        var missingSettings = await handler.HandleAsync(
            new CreateRuntimeEnvironmentCommand(
                CreateProfile("missing-settings") with
                {
                    EnvironmentSettings = null!,
                    RuntimeSettings = null!,
                }
            ),
            CancellationToken.None
        );
        var duplicate = await handler.HandleAsync(
            new CreateRuntimeEnvironmentCommand(CreateProfile(" SHARED ")),
            CancellationToken.None
        );

        Assert.Equal(RuntimeEnvironmentOperationStatus.ValidationFailed, missingProfile.Status);
        Assert.Contains("profile", missingProfile.ValidationErrors.Keys);
        Assert.Equal(RuntimeEnvironmentOperationStatus.ValidationFailed, missingSettings.Status);
        Assert.Contains("environmentSettings", missingSettings.ValidationErrors.Keys);
        Assert.Contains("runtimeSettings", missingSettings.ValidationErrors.Keys);
        Assert.Equal(RuntimeEnvironmentOperationStatus.Conflict, duplicate.Status);
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
        var environments = new FakeRuntimeEnvironmentStore(original);
        var handler = new UpdateRuntimeEnvironmentCommandHandler(environments);
        var suppliedTimestamp = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var update = CreateMockProfile(" PRODUCTION ") with
        {
            DisplayName = " Updated mock runtime ",
            Enabled = false,
            EnvironmentSettings = new EnvironmentProviderSettings
            {
                WorkspaceRoot = " /srv/agent-controller/runs ",
            },
            CreatedAt = suppliedTimestamp,
            UpdatedAt = suppliedTimestamp,
        };
        var before = DateTimeOffset.UtcNow;

        var result = await handler.HandleAsync(
            new UpdateRuntimeEnvironmentCommand(" production ", update),
            CancellationToken.None
        );
        var after = DateTimeOffset.UtcNow;

        Assert.Equal(RuntimeEnvironmentOperationStatus.Succeeded, result.Status);
        var persisted = Assert.IsType<RuntimeEnvironmentProfile>(environments.LastUpdated);
        Assert.Equal("production", persisted.Key);
        Assert.Equal("Updated mock runtime", persisted.DisplayName);
        Assert.False(persisted.Enabled);
        Assert.Equal("MockPiMateria", persisted.RuntimeProvider);
        Assert.Equal("/srv/agent-controller/runs", persisted.EnvironmentSettings.WorkspaceRoot);
        Assert.Equal(createdAt, persisted.CreatedAt);
        Assert.InRange(persisted.UpdatedAt, before, after);
        Assert.Same(persisted, result.Environment);
    }

    [Fact]
    public async Task Update_RejectsImmutableKeyChangesAndReturnsNotFoundForMissingProfile()
    {
        var environments = new FakeRuntimeEnvironmentStore(CreateProfile("original"));
        var handler = new UpdateRuntimeEnvironmentCommandHandler(environments);

        var changedKey = await handler.HandleAsync(
            new UpdateRuntimeEnvironmentCommand("original", CreateProfile("replacement")),
            CancellationToken.None
        );
        var missing = await handler.HandleAsync(
            new UpdateRuntimeEnvironmentCommand("missing", CreateProfile("missing")),
            CancellationToken.None
        );

        Assert.Equal(RuntimeEnvironmentOperationStatus.ValidationFailed, changedKey.Status);
        Assert.Contains(
            "immutable",
            changedKey.ValidationErrors["key"].Single(),
            StringComparison.OrdinalIgnoreCase
        );
        Assert.Equal(RuntimeEnvironmentOperationStatus.NotFound, missing.Status);
        Assert.Null(environments.LastUpdated);
    }

    [Fact]
    public async Task Delete_ReturnsConflictWhileARepositoryReferencesEnvironment()
    {
        var environments = new FakeRuntimeEnvironmentStore(CreateProfile("shared"));
        var repositories = new FakeRepositoryStore(
            new RepositoryProfile { Key = "service-b", RuntimeEnvironmentKey = " SHARED " },
            new RepositoryProfile { Key = "service-a", RuntimeEnvironmentKey = "shared" }
        );
        var handler = new DeleteRuntimeEnvironmentCommandHandler(environments, repositories);

        var result = await handler.HandleAsync(
            new DeleteRuntimeEnvironmentCommand(" SHARED "),
            CancellationToken.None
        );

        Assert.Equal(RuntimeEnvironmentOperationStatus.Conflict, result.Status);
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
        var environments = new FakeRuntimeEnvironmentStore(CreateProfile("temporary"));
        var handler = new DeleteRuntimeEnvironmentCommandHandler(
            environments,
            new FakeRepositoryStore()
        );

        var deleted = await handler.HandleAsync(
            new DeleteRuntimeEnvironmentCommand(" TEMPORARY "),
            CancellationToken.None
        );
        var missing = await handler.HandleAsync(
            new DeleteRuntimeEnvironmentCommand("temporary"),
            CancellationToken.None
        );
        var invalid = await handler.HandleAsync(
            new DeleteRuntimeEnvironmentCommand("bad key"),
            CancellationToken.None
        );

        Assert.Equal(RuntimeEnvironmentOperationStatus.Succeeded, deleted.Status);
        Assert.Equal("temporary", environments.LastDeletedKey);
        Assert.Equal(RuntimeEnvironmentOperationStatus.NotFound, missing.Status);
        Assert.Equal(RuntimeEnvironmentOperationStatus.ValidationFailed, invalid.Status);
    }

    [Fact]
    public void AddApplicationHandlers_RegistersRuntimeEnvironmentCommandsAndQueries()
    {
        var services = new ServiceCollection();

        services.AddApplicationHandlers();

        AssertRegistration<
            ICommandHandler<CreateRuntimeEnvironmentCommand, RuntimeEnvironmentOperationResult>,
            CreateRuntimeEnvironmentCommandHandler
        >(services);
        AssertRegistration<
            ICommandHandler<UpdateRuntimeEnvironmentCommand, RuntimeEnvironmentOperationResult>,
            UpdateRuntimeEnvironmentCommandHandler
        >(services);
        AssertRegistration<
            ICommandHandler<DeleteRuntimeEnvironmentCommand, RuntimeEnvironmentOperationResult>,
            DeleteRuntimeEnvironmentCommandHandler
        >(services);
        AssertRegistration<
            IQueryHandler<ListRuntimeEnvironmentsQuery, IReadOnlyList<RuntimeEnvironmentProfile>>,
            ListRuntimeEnvironmentsQueryHandler
        >(services);
        AssertRegistration<
            IQueryHandler<GetRuntimeEnvironmentByKeyQuery, RuntimeEnvironmentOperationResult>,
            GetRuntimeEnvironmentByKeyQueryHandler
        >(services);
    }

    private static RuntimeEnvironmentProfile CreateProfile(string key) =>
        new()
        {
            Key = key,
            DisplayName = $"{key} runtime",
            Enabled = true,
            EnvironmentProvider = "LocalWorkspace",
            EnvironmentSettings = new EnvironmentProviderSettings
            {
                WorkspaceRoot = "/var/lib/agent-controller/runs",
            },
            RuntimeProvider = "PiMateria",
            RuntimeSettings = new RuntimeProviderSettings
            {
                PiExecutablePath = "/usr/local/bin/pi",
                ControllerBaseUrl = "https://controller.example.test",
                PtyWrapperPath = "script",
                PtyWrapperArgs = "-qfc",
                Loadouts = new Dictionary<ExecutionKind, string>
                {
                    [ExecutionKind.NewWork] = "new-work",
                    [ExecutionKind.Rework] = "rework",
                },
                ForwardEnvironmentVariables = new Dictionary<string, string>
                {
                    ["AZURE_DEVOPS_EXT_PAT"] = "CONTROLLER_ADO_PAT",
                },
            },
        };

    private static RuntimeEnvironmentProfile CreateMockProfile(string key) =>
        CreateProfile(key) with
        {
            RuntimeProvider = "mockpimateria",
            RuntimeSettings = new RuntimeProviderSettings
            {
                PiExecutablePath = null,
                ControllerBaseUrl = null,
                PtyWrapperPath = null,
                PtyWrapperArgs = null,
                Loadouts = new Dictionary<ExecutionKind, string>(),
                ForwardEnvironmentVariables = new Dictionary<string, string>(),
            },
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

    private sealed class FakeRuntimeEnvironmentStore : IRuntimeEnvironmentStore
    {
        private readonly Dictionary<string, RuntimeEnvironmentProfile> _profiles;

        public FakeRuntimeEnvironmentStore(params RuntimeEnvironmentProfile[] profiles)
        {
            _profiles = profiles.ToDictionary(profile => profile.Key, StringComparer.Ordinal);
        }

        public string? LastReadKey { get; private set; }

        public RuntimeEnvironmentProfile? LastCreated { get; private set; }

        public RuntimeEnvironmentProfile? LastUpdated { get; private set; }

        public string? LastDeletedKey { get; private set; }

        public Task<IReadOnlyList<RuntimeEnvironmentProfile>> ListAsync(
            CancellationToken cancellationToken
        )
        {
            IReadOnlyList<RuntimeEnvironmentProfile> profiles = _profiles
                .Values.OrderBy(profile => profile.Key, StringComparer.Ordinal)
                .ToList();
            return Task.FromResult(profiles);
        }

        public Task<RuntimeEnvironmentProfile?> GetByKeyAsync(
            string key,
            CancellationToken cancellationToken
        )
        {
            LastReadKey = key;
            _profiles.TryGetValue(key, out var profile);
            return Task.FromResult(profile);
        }

        public Task<bool> CreateAsync(
            RuntimeEnvironmentProfile profile,
            CancellationToken cancellationToken
        )
        {
            LastCreated = profile;
            return Task.FromResult(_profiles.TryAdd(profile.Key, profile));
        }

        public Task<bool> UpdateAsync(
            RuntimeEnvironmentProfile profile,
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

using AgentController.Application;
using AgentController.Application.Abstractions;
using AgentController.Application.Services;
using AgentController.Infrastructure;
using AgentController.Infrastructure.Data;
using AgentController.Infrastructure.Data.Repositories;
using AgentController.Infrastructure.Options;
using AgentController.Infrastructure.Secrets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Service collection extensions for registering AgentController infrastructure services.
/// </summary>
public static class AgentControllerServiceCollectionExtensions
{
    /// <summary>
    /// Registers all AgentController options from configuration with validation-on-start.
    /// </summary>
    public static IServiceCollection AddAgentControllerOptions(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
        services
            .AddOptions<AgentControllerOptions>()
            .Bind(configuration.GetSection(AgentControllerOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services
            .AddOptions<PersistenceOptions>()
            .Bind(configuration.GetSection(PersistenceOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services
            .AddOptions<WorkSourceOptions>()
            .Bind(configuration.GetSection(WorkSourceOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Register WorkSourceOptionsView as a projection of WorkSourceOptions.
        // This allows the Application layer to read ActiveState/CompletedState
        // via IOptionsMonitor<IWorkSourceOptions> without depending on the
        // Infrastructure WorkSourceOptions type directly.
        services.Configure<WorkSourceOptionsView>(wsOptionsView =>
        {
            var wsOptions = configuration
                .GetSection(WorkSourceOptions.SectionName)
                .Get<WorkSourceOptions>();

            if (wsOptions is not null)
            {
                wsOptionsView.OrganizationUrl = wsOptions.OrganizationUrl;
                wsOptionsView.Project = wsOptions.Project;
                wsOptionsView.ActiveState = wsOptions.ActiveState;
                wsOptionsView.CompletedState = wsOptions.CompletedState;
                wsOptionsView.TagPrefix = wsOptions.TagPrefix;
            }
        });

        services
            .AddOptions<SourceControlOptions>()
            .Bind(configuration.GetSection(SourceControlOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services
            .AddOptions<EnvironmentProviderOptions>()
            .Bind(configuration.GetSection(EnvironmentProviderOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services
            .AddOptions<RuntimeOptions>()
            .Bind(configuration.GetSection(RuntimeOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services
            .AddOptions<Dictionary<string, RepositoryProfileOptions>>()
            .Bind(configuration.GetSection(RepositoriesOptions.SectionName))
            .Validate(ValidateRepositoryProfiles, "Repository profile validation failed.")
            .ValidateOnStart();

        services
            .AddOptions<AzureDevOpsBoardsOptions>()
            .Bind(configuration.GetSection(AzureDevOpsBoardsOptions.SectionName));

        services
            .AddOptions<LocalWorkOptions>()
            .Bind(configuration.GetSection(LocalWorkOptions.SectionName));

        services
            .AddOptions<LocalFeedbackOptions>()
            .Bind(configuration.GetSection(LocalFeedbackOptions.SectionName));

        services
            .AddOptions<FeedbackOptions>()
            .Bind(configuration.GetSection(FeedbackOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton<IConfiguredProfileSource, ConfiguredProfileSource>();

        return services;
    }

    /// <summary>
    /// Registers the EF Core <see cref="AgentControllerDbContext"/> with a SQLite
    /// connection string from <see cref="PersistenceOptions"/>. The context is registered
    /// as scoped so each request or worker poll cycle gets a fresh unit-of-work.
    ///
    /// This method never calls <c>MigrateAsync()</c> or <c>EnsureCreated()</c>.
    /// Schema migrations are owned by the dedicated <c>AgentController.Migrations</c>
    /// console application.
    /// </summary>
    public static IServiceCollection AddAgentControllerDbContext(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
        var persistenceOptions =
            configuration.GetSection(PersistenceOptions.SectionName).Get<PersistenceOptions>()
            ?? new PersistenceOptions();

        var connectionString = PersistenceConnectionResolver.Resolve(persistenceOptions);

        services.AddDbContext<AgentControllerDbContext>(options =>
        {
            options.UseSqlite(
                connectionString,
                sqliteOptions => sqliteOptions.MigrationsAssembly("AgentController.Migrations")
            );
        });

        return services;
    }

    /// <summary>
    /// Registers EF Core-backed repository implementations for all application-layer
    /// persistence contracts (<see cref="IWorkItemStore"/>, <see cref="IAgentRunStore"/>,
    /// <see cref="ILifecycleEventStore"/>, <see cref="IEnvironmentStore"/>,
    /// <see cref="IRepositoryStore"/>, <see cref="IWorkSourceEnvironmentStore"/>,
    /// <see cref="IRuntimeEnvironmentStore"/>, <see cref="IReworkCycleStore"/>,
    /// <see cref="IReworkFeedbackStore"/>).
    ///
    /// Repositories are registered as scoped so each request or worker poll cycle
    /// gets a fresh unit-of-work backed by the same <see cref="AgentControllerDbContext"/>.
    ///
    /// Requires <see cref="AddAgentControllerDbContext"/> to be called first.
    /// </summary>
    public static IServiceCollection AddAgentControllerRepositories(
        this IServiceCollection services
    )
    {
        services.AddScoped<IWorkItemStore, EfWorkItemStore>();
        services.AddScoped<IAgentRunStore, EfAgentRunStore>();
        services.AddScoped<ILifecycleEventStore, EfLifecycleEventStore>();
        services.AddScoped<IEnvironmentStore, EfEnvironmentStore>();
        services.AddScoped<IRepositoryStore, EfRepositoryStore>();
        services.AddScoped<IWorkSourceEnvironmentStore, EfWorkSourceEnvironmentStore>();
        services.AddScoped<IRuntimeEnvironmentStore, EfRuntimeEnvironmentStore>();
        services.AddScoped<IReworkCycleStore, EfReworkCycleStore>();
        services.AddScoped<IReworkFeedbackStore, EfReworkFeedbackStore>();
        services.AddScoped<IRepositoryHostConnectionStore, EfRepositoryHostConnectionStore>();

        return services;
    }

    /// <summary>
    /// Registers the <see cref="RunLifecycleService"/> as a scoped <see cref="IRunLifecycleService"/>.
    /// Coordinates <see cref="IAgentRunStore"/>, <see cref="ILifecycleEventStore"/>, and
    /// <see cref="IWorkItemStore"/> for consistent run lifecycle transitions.
    ///
    /// Requires <see cref="AddAgentControllerRepositories"/> to be called first.
    /// </summary>
    public static IServiceCollection AddAgentControllerLifecycleService(
        this IServiceCollection services
    )
    {
        services.AddScoped<IRunLifecycleService, RunLifecycleService>();

        // Ensure secret stores are registered for AzureDevOpsPatResolver dependency.
        AddAgentControllerSecretStores(services);

        // Shared ADO client factory — used by both work-source (Boards) and repo-host (Repos) paths.
        services.AddSingleton<AzureDevOpsClientFactory>();

        // Shared PAT resolver used by both work-source (Boards) and repo-host (Repos) ADO paths.
        services.AddSingleton<AzureDevOpsPatResolver>();

        // Boards client factory delegates to the shared AzureDevOpsClientFactory.
        services.TryAddSingleton<IAzureDevOpsBoardsClientFactory, AzureDevOpsBoardsClientFactory>();

        return services;
    }

    /// <summary>
    /// Registers the <see cref="LocalFakeWorkSource"/> as a singleton <see cref="IWorkSource"/>
    /// implementation backed by the persisted <see cref="IWorkItemStore"/>. Applies
    /// configured <see cref="WorkSourceOptions"/> for tag/state eligibility filtering.
    ///
    /// <see cref="LocalFakeWorkSource"/> uses <see cref="IServiceScopeFactory"/> internally
    /// to resolve the scoped <see cref="IWorkItemStore"/> per operation, so it is safe for
    /// consumption by singleton consumers such as <see cref="BackgroundService"/>.
    ///
    /// Requires <see cref="AddAgentControllerRepositories"/> to be called first.
    /// </summary>
    public static IServiceCollection AddAgentControllerLocalFakeWorkSource(
        this IServiceCollection services
    )
    {
        services.AddSingleton<IWorkSource, LocalFakeWorkSource>();

        return services;
    }

    /// <summary>
    /// Registers the <see cref="LocalFileWorkSource"/> as a singleton <see cref="IWorkSource"/>
    /// implementation that reads work item definitions from the <c>localWork</c> configuration
    /// section and upserts them into the persisted <see cref="IWorkItemStore"/> on first use.
    ///
    /// When <c>workSource:provider</c> is <c>"LocalFile"</c>, use this method instead of
    /// <see cref="AddAgentControllerLocalFakeWorkSource"/> to seed work items declaratively
    /// from configuration without API calls.
    ///
    /// <see cref="LocalFileWorkSource"/> uses <see cref="IServiceScopeFactory"/> internally
    /// to resolve the scoped <see cref="IWorkItemStore"/> per operation, so it is safe for
    /// consumption by singleton consumers such as <see cref="BackgroundService"/>.
    ///
    /// Requires <see cref="AddAgentControllerRepositories"/> to be called first.
    /// </summary>
    public static IServiceCollection AddAgentControllerLocalFileWorkSource(
        this IServiceCollection services
    )
    {
        services.AddSingleton<IWorkSource, LocalFileWorkSource>();

        return services;
    }

    /// <summary>
    /// Registers deterministic no-op infrastructure providers suitable for DI seeding
    /// before real providers are wired.
    /// </summary>
    public static IServiceCollection AddAgentControllerNoOpProviders(
        this IServiceCollection services
    )
    {
        services.AddSingleton<IWorkSource, NoOpWorkSource>();
        services.AddSingleton<ISourceControlProvider, NoOpSourceControlProvider>();

        services.TryAddSingleton<NoOpEnvironmentProvider>();
        services.TryAddSingleton<LocalWorkspaceEnvironmentProvider>();
        services.TryAddSingleton<NoOpAgentRuntime>();
        services.TryAddSingleton<PiMateriaRuntime>();
        services.TryAddSingleton<MockPiMateriaRuntime>();
        services.TryAddSingleton<IExecutionProviderResolver, ExecutionProviderResolver>();

        services.AddSingleton<IEnvironmentProvider>(serviceProvider =>
            serviceProvider.GetRequiredService<NoOpEnvironmentProvider>()
        );
        services.AddSingleton<IAgentRuntime>(serviceProvider =>
            serviceProvider.GetRequiredService<NoOpAgentRuntime>()
        );

        return services;
    }

    /// <summary>
    /// Registers the <see cref="LocalGitSourceControlProvider"/> as a singleton
    /// <see cref="ISourceControlProvider"/> that uses <c>git clone</c> for all
    /// clone URL types: local paths, <c>file://</c> URLs, and remote URLs.
    ///
    /// Callers should register this <em>after</em>
    /// <see cref="AddAgentControllerNoOpProviders"/> so the last-registered
    /// <see cref="ISourceControlProvider"/> wins.
    /// </summary>
    public static IServiceCollection AddAgentControllerLocalGitSourceControl(
        this IServiceCollection services
    )
    {
        services.AddSingleton<ISourceControlProvider, LocalGitSourceControlProvider>();

        return services;
    }

    /// <summary>
    /// Registers the <see cref="LocalGitRepositoryMaterializer"/> as a singleton
    /// <see cref="IRepositoryMaterializer"/> that clones repositories into the local
    /// filesystem using <c>git clone</c> with transport-appropriate credential injection:
    /// <list type="bullet">
    ///   <item><b>HTTPS+PAT</b>: injects credentials via <c>git http.extraHeader</c></item>
    ///   <item><b>SSH</b>: uses the configured SSH key with <c>GIT_SSH_COMMAND</c></item>
    ///   <item><b>Local</b>: native git clone for local paths</item>
    /// </list>
    ///
    /// Requires <see cref="AddAgentControllerSecretStores"/> to be called first
    /// (for <see cref="IManagedSecretStore"/> dependency).
    ///
    /// Callers should register this <em>after</em>
    /// <see cref="AddAgentControllerNoOpProviders"/> so the last-registered
    /// <see cref="IRepositoryMaterializer"/> wins.
    /// </summary>
    public static IServiceCollection AddAgentControllerLocalGitRepositoryMaterializer(
        this IServiceCollection services
    )
    {
        services.AddSingleton<IRepositoryMaterializer, LocalGitRepositoryMaterializer>();

        return services;
    }

    /// <summary>
    /// Registers the <see cref="LocalWorkspaceEnvironmentProvider"/> as a singleton
    /// <see cref="IEnvironmentProvider"/> that creates per-run local workspace
    /// directories under <c>{runRoot}/{runId}/</c>.
    ///
    /// Callers should register this <em>after</em>
    /// <see cref="AddAgentControllerNoOpProviders"/> so the last-registered
    /// <see cref="IEnvironmentProvider"/> wins.
    /// </summary>
    public static IServiceCollection AddAgentControllerLocalWorkspaceEnvironment(
        this IServiceCollection services
    )
    {
        services.TryAddSingleton<LocalWorkspaceEnvironmentProvider>();
        services.AddSingleton<IEnvironmentProvider>(serviceProvider =>
            serviceProvider.GetRequiredService<LocalWorkspaceEnvironmentProvider>()
        );

        return services;
    }

    /// <summary>
    /// Registers the <see cref="PiMateriaRuntime"/> as a singleton
    /// <see cref="IAgentRuntime"/> that launches <c>pi</c> as a detached CLI process
    /// via <c>pi "/materia loadout {loadout}" "/materia cast {task}"</c>.
    /// The loadout is resolved from <c>RuntimeOptions.Loadouts[spec.ExecutionKind]</c>
    /// (defaulting to <c>ADO-Build-NewWork</c> for new work or <c>ADO-Build-Rework</c>
    /// for rework feedback).
    /// The launched job reports important status back only via webhook;
    /// the controller treats the launch as fire-and-forget.
    ///
    /// Requires <see cref="AddAgentControllerOptions"/> to be called first
    /// (for <see cref="RuntimeOptions"/> and <see cref="AgentControllerOptions"/> binding).
    /// Requires <see cref="RuntimeOptions.ControllerBaseUrl"/> to be configured so the
    /// runtime can hand pi-materia the webhook URL.
    ///
    /// Callers should register this <em>after</em>
    /// <see cref="AddAgentControllerNoOpProviders"/> so the last-registered
    /// <see cref="IAgentRuntime"/> wins.
    /// </summary>
    public static IServiceCollection AddAgentControllerPiMateriaRuntime(
        this IServiceCollection services
    )
    {
        services.TryAddSingleton<PiMateriaRuntime>();
        services.AddSingleton<IAgentRuntime>(serviceProvider =>
            serviceProvider.GetRequiredService<PiMateriaRuntime>()
        );

        return services;
    }

    /// <summary>
    /// Registers the <see cref="MockPiMateriaRuntime"/> as a singleton
    /// <see cref="IAgentRuntime"/> that simulates a pi-materia runtime by
    /// emitting a deterministic sequence of runtime events in-process.
    ///
    /// The mock runtime fires <c>runtime.accepted</c>, <c>runtime.heartbeat</c>,
    /// <c>runtime.status</c>, and <c>runtime.completed</c> events automatically
    /// after <see cref="IAgentRuntime.StartAsync"/> is called, driving the run
    /// through to completion without requiring an external process or HTTP calls.
    ///
    /// Requires <see cref="AddAgentControllerOptions"/> to be called first
    /// (for <see cref="RuntimeOptions"/> binding).
    ///
    /// Callers should register this <em>after</em>
    /// <see cref="AddAgentControllerNoOpProviders"/> so the last-registered
    /// <see cref="IAgentRuntime"/> wins.
    /// </summary>
    public static IServiceCollection AddAgentControllerMockPiMateriaRuntime(
        this IServiceCollection services
    )
    {
        services.TryAddSingleton<MockPiMateriaRuntime>();
        services.AddSingleton<IAgentRuntime>(serviceProvider =>
            serviceProvider.GetRequiredService<MockPiMateriaRuntime>()
        );

        return services;
    }

    /// <summary>
    /// Registers the Azure DevOps Boards implementation as a singleton
    /// <see cref="IWorkSource"/> backed by <see cref="IAzureDevOpsBoardsClient"/>.
    ///
    /// This method:
    /// <list type="number">
    ///   <item>
    ///     Registers <see cref="AzureDevOpsBoardsClient"/> as a scoped
    ///     <see cref="IAzureDevOpsBoardsClient"/> with a managed <see cref="HttpClient"/>.
    ///   </item>
    ///   <item>
    ///     Registers <see cref="AzureDevOpsBoardsWorkSource"/> as a singleton
    ///     <see cref="IWorkSource"/>. It uses <see cref="IServiceScopeFactory"/>
    ///     internally to resolve scoped services per operation, making it safe
    ///     for consumption by singleton consumers such as <see cref="BackgroundService"/>.
    ///   </item>
    /// </list>
    ///
    /// Requires <see cref="AddAgentControllerOptions"/> to be called first
    /// (for <see cref="WorkSourceOptions"/> and <see cref="AzureDevOpsBoardsOptions"/>).
    ///
    /// Callers should register this <em>after</em>
    /// <see cref="AddAgentControllerNoOpProviders"/> or
    /// <see cref="AddAgentControllerLocalFakeWorkSource"/> so the last-registered
    /// <see cref="IWorkSource"/> wins.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="validateConnection">
    /// When <c>true</c> (default), validates at registration time that required
    /// Azure DevOps settings (organization URL, project, PAT) are configured.
    /// Set to <c>false</c> to skip eager validation (useful in tests or when
    /// the work source provider is not AzureDevOpsBoards).
    /// </param>
    public static IServiceCollection AddAgentControllerAzureDevOpsBoardsWorkSource(
        this IServiceCollection services,
        bool validateConnection = true
    )
    {
        // Register the Azure DevOps Boards HTTP client as scoped.
        // Each operation (poll cycle, request) gets a fresh client.
        services.AddScoped<IAzureDevOpsBoardsClient>(sp =>
        {
            var boardsOptions = sp.GetRequiredService<IOptions<AzureDevOpsBoardsOptions>>().Value;
            var workSourceOptions = sp.GetRequiredService<IOptions<WorkSourceOptions>>().Value;

            // Derive BaseUrl and Project from WorkSourceOptions into BoardsOptions
            boardsOptions.BaseUrl = workSourceOptions.OrganizationUrl;
            boardsOptions.Project = workSourceOptions.Project;

            if (validateConnection)
            {
                AzureDevOpsBoardsValidator.Validate(workSourceOptions, boardsOptions);
            }

            var http = new HttpClient();
            var logger = sp.GetRequiredService<ILogger<AzureDevOpsBoardsClient>>();
            return new AzureDevOpsBoardsClient(http, boardsOptions, logger);
        });

        // Register the work source implementation as singleton.
        // It uses IServiceScopeFactory to resolve scoped profile/client services
        // per operation.
        services.AddSingleton<IWorkSource, AzureDevOpsBoardsWorkSource>();

        // Register startup validator for configured ADO board states.
        // Validates ActiveState, CompletedState, and EligibleStates against
        // the actual valid System.State values for the configured project/WIT.
        // Throws during startup if any configured state is invalid.
        services.AddHostedService<AzureDevOpsBoardStateStartupValidator>();

        // Register secret stores so IManagedSecretStore is available for PAT resolution.
        AddAgentControllerSecretStores(services);

        // Shared ADO client factory — used by both work-source (Boards) and repo-host (Repos) paths.
        services.TryAddSingleton<AzureDevOpsClientFactory>();

        // Register the shared PAT resolver used by both work-source (Boards)
        // and repo-host (Repos) ADO paths. Routes resolution through IManagedSecretStore
        // with backward compatibility for legacy "ENV:NAME" and direct PAT forms.
        services.TryAddSingleton<AzureDevOpsPatResolver>();

        // Boards client factory delegates to the shared AzureDevOpsClientFactory.
        services.TryAddSingleton<IAzureDevOpsBoardsClientFactory, AzureDevOpsBoardsClientFactory>();

        // Register the Azure DevOps connectivity verifier with the provider-keyed resolver.
        // Keyed by "AzureDevOpsBoards" and "AzureDevOpsRepos" provider strings.
        services.AddWorkSourceConnectivityVerifier<AzureDevOpsConnectivityVerifier>(
            "AzureDevOpsBoards",
            "AzureDevOpsRepos"
        );

        // Register the Azure DevOps Repos repository host for the provider-keyed resolver.
        // Uses IManagedSecretStore for PAT resolution (not Environment.GetEnvironmentVariable).
        // Reuses AzureDevOpsBoardsClient for HTTP operations.
        // Repos client factory delegates to the shared AzureDevOpsClientFactory.
        services.TryAddSingleton<IAzureDevOpsReposClientFactory, AzureDevOpsReposClientFactory>();
        services.AddRepositoryHost<AzureDevOpsReposRepositoryHost>("AzureDevOpsRepos");

        return services;
    }

    /// <summary>
    /// Registers the <see cref="LocalFeedbackSource"/> as a singleton
    /// <see cref="IFeedbackSource"/> that returns deterministic
    /// <see cref="Application.ReworkSignal"/> instances from the
    /// <c>localFeedback</c> configuration section.
    ///
    /// Also registers <see cref="LocalPrLabelSource"/> as the
    /// <see cref="IPrLabelSource"/> for marker-gate label lookups.
    ///
    /// Mirrors <see cref="AddAgentControllerLocalFileWorkSource"/>: definitions
    /// are validated and cached on first use, then returned for any
    /// <see cref="Application.PrUnderTest"/> whose <c>PullRequestId</c> matches
    /// a configured signal.
    ///
    /// Requires <see cref="AddAgentControllerOptions"/> to be called first
    /// (for <see cref="LocalFeedbackOptions"/> binding).
    /// </summary>
    public static IServiceCollection AddAgentControllerLocalFeedbackSource(
        this IServiceCollection services
    )
    {
        services.AddSingleton<IFeedbackSource, LocalFeedbackSource>();
        services.AddSingleton<IPrLabelSource, LocalPrLabelSource>();

        return services;
    }

    /// <summary>
    /// Registers the Azure DevOps Repos implementations for the feedback pipeline:
    /// <see cref="AzureDevOpsReposFeedbackSource"/> as <see cref="IFeedbackSource"/>
    /// and <see cref="AzureDevOpsReposPrLabelSource"/> as <see cref="IPrLabelSource"/>.
    ///
    /// Both use the same HttpClient and AzureDevOpsBoardsOptions wiring as the
    /// Azure DevOps Boards work source.
    ///
    /// Requires <see cref="AddAgentControllerOptions"/> to be called first
    /// (for <see cref="AzureDevOpsBoardsOptions"/> and <see cref="WorkSourceOptions"/>).
    /// </summary>
    public static IServiceCollection AddAgentControllerAzureDevOpsReposFeedbackSource(
        this IServiceCollection services
    )
    {
        // Register the feedback source (thread fetcher) as scoped.
        services.AddScoped<IFeedbackSource>(sp =>
        {
            var boardsOptions = sp.GetRequiredService<IOptions<AzureDevOpsBoardsOptions>>().Value;
            var workSourceOptions = sp.GetRequiredService<IOptions<WorkSourceOptions>>().Value;

            boardsOptions.BaseUrl = workSourceOptions.OrganizationUrl;
            boardsOptions.Project = workSourceOptions.Project;

            var http = new HttpClient();
            return new AzureDevOpsReposFeedbackSource(http, boardsOptions);
        });

        // Register the PR label source (marker gate) as singleton.
        // The filter pipeline is a singleton and depends on IPrLabelSource,
        // so the label source must not be scoped.
        services.AddSingleton<IPrLabelSource>(sp =>
        {
            var boardsOptions = sp.GetRequiredService<IOptions<AzureDevOpsBoardsOptions>>().Value;
            var workSourceOptions = sp.GetRequiredService<IOptions<WorkSourceOptions>>().Value;
            var logger = sp.GetRequiredService<ILogger<AzureDevOpsReposPrLabelSource>>();

            boardsOptions.BaseUrl = workSourceOptions.OrganizationUrl;
            boardsOptions.Project = workSourceOptions.Project;

            var http = new HttpClient();
            return new AzureDevOpsReposPrLabelSource(http, boardsOptions, logger);
        });

        return services;
    }

    /// <summary>
    /// Registers the <see cref="ReviewFeedbackFilterPipeline"/> as a singleton
    /// service that applies the 5-step load-bearing filter pipeline to raw
    /// rework signals.
    ///
    /// Also registers a no-op <see cref="IPrLabelSource"/> as the default.
    /// Provider-specific registrations (<see cref="AddAgentControllerLocalFeedbackSource"/>,
    /// <see cref="AddAgentControllerAzureDevOpsReposFeedbackSource"/>)
    /// override this with real implementations.
    /// </summary>
    public static IServiceCollection AddAgentControllerFeedbackFilterPipeline(
        this IServiceCollection services
    )
    {
        // No-op label source: marker gate fails-closed for all PRs.
        // Provider-specific registrations override this.
        services.AddSingleton<IPrLabelSource, NoOpPrLabelSource>();
        services.AddSingleton<ReviewFeedbackFilterPipeline>();

        return services;
    }

    /// <summary>
    /// Registers the complete feedback polling pipeline: provider-selected
    /// <see cref="IFeedbackSource"/> and <see cref="IPrLabelSource"/>, the
    /// <see cref="ReviewFeedbackFilterPipeline"/>, and the
    /// <see cref="AgentController.Api.FeedbackPollingWorker"/> hosted service.
    ///
    /// Provider selection is driven by <c>feedback:provider</c> configuration:
    /// <list type="bullet">
    ///   <item><description><c>AzureDevOpsRepos</c> — registers <see cref="AzureDevOpsReposFeedbackSource"/> and <see cref="AzureDevOpsReposPrLabelSource"/>.</description></item>
    ///   <item><description><c>Local</c> — registers <see cref="LocalFeedbackSource"/> and <see cref="LocalPrLabelSource"/>.</description></item>
    ///   <item><description><c>None</c> (default) — registers a no-op <see cref="IFeedbackSource"/>; the worker is still registered but returns no signals.</description></item>
    /// </list>
    ///
    /// This is a convenience wrapper that replaces the inline feedback wiring in
    /// <c>Program.cs</c>. It consolidates:
    /// <list type="number">
    ///   <item>Feedback source provider selection and registration.</item>
    ///   <item>Filter pipeline registration (always, regardless of provider).</item>
    ///   <item>FeedbackPollingWorker hosted service registration.</item>
    /// </list>
    ///
    /// Requires <see cref="AddAgentControllerOptions"/> to be called first
    /// (for <see cref="FeedbackOptions"/> and <see cref="AzureDevOpsBoardsOptions"/> binding).
    /// Requires <see cref="AddAgentControllerRepositories"/> to be called first
    /// (for <see cref="IReworkCycleStore"/> and <see cref="IReworkFeedbackStore"/>).
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">
    /// Configuration used to read <c>feedback:provider</c> and
    /// <c>workSource</c>/<c>azureDevOpsBoards</c> sections for Azure DevOps wiring.
    /// </param>
    public static IServiceCollection AddAgentControllerFeedback(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
        var feedbackProvider =
            configuration.GetValue<string>($"{FeedbackOptions.SectionName}:provider") ?? "None";

        // ── Filter pipeline (always registered, must come first) ──
        // The filter pipeline registers a no-op IPrLabelSource as the default.
        // Provider-specific registrations below override it.
        AddAgentControllerFeedbackFilterPipeline(services);

        // ── Provider selection ────────────────────────────────────
        switch (feedbackProvider)
        {
            case "AzureDevOpsRepos":
                AddAgentControllerAzureDevOpsReposFeedbackSource(services);
                break;

            case "Local":
                AddAgentControllerLocalFeedbackSource(services);
                break;

            case "None":
            default:
                // No-op feedback source: PollAsync returns empty.
                // The no-op IPrLabelSource is registered by the filter pipeline above.
                services.AddSingleton<IFeedbackSource, NoOpFeedbackSource>();
                break;
        }

        // Note: FeedbackPollingWorker is registered in Program.cs via
        // AddHostedService<FeedbackPollingWorker>() because the worker
        // lives in the Api project and Infrastructure cannot reference it.

        return services;
    }

    /// <summary>
    /// Validates each repository profile in the configuration dictionary.
    /// Ensures every profile has a non-empty <c>cloneUrl</c> so that
    /// misconfigured profiles fail fast at startup instead of silently no-op'ing
    /// or hanging at clone time.
    /// </summary>
    private static bool ValidateRepositoryProfiles(
        Dictionary<string, RepositoryProfileOptions> profiles
    )
    {
        if (profiles is null || profiles.Count == 0)
        {
            return true; // No profiles configured — not an error
        }

        foreach (var entry in profiles)
        {
            if (string.IsNullOrWhiteSpace(entry.Value?.CloneUrl))
            {
                return false; // Missing cloneUrl — validation error
            }
        }

        return true;
    }

    /// <summary>
    /// Registers <see cref="IManagedSecretStore"/> implementations backed by environment variables
    /// and the database (EF Core <see cref="Data.Entities.SecretEntity"/> table).
    ///
    /// A <see cref="Secrets.SecretStoreResolver"/> dispatches to the correct store
    /// based on <see cref="Domain.SecretReference.Kind"/>:
    /// <list type="bullet">
    ///   <item><description><c>"EnvVar"</c> → <see cref="Secrets.EnvVarSecretStore"/> (read-only, reads environment variables)</description></item>
    ///   <item><description><c>"Db"</c> → <see cref="Secrets.DbSecretStore"/> (read/write, persisted in Secrets table)</description></item>
    /// </list>
    ///
    /// Optionally accepts an <see cref="Secrets.ISecretProtector"/> for encrypted-at-rest
    /// storage of database-backed secrets. If no protector is registered, values are
    /// stored as plaintext (with a TODO for key rotation).
    ///
    /// Requires <see cref="AddAgentControllerDbContext"/> to be called first (for DbSecretStore).
    /// </summary>
    public static IServiceCollection AddAgentControllerSecretStores(
        this IServiceCollection services
    )
    {
        // Register the EnvVar-backed store (read-only, reads Environment.GetEnvironmentVariable).
        services.AddSingleton<EnvVarSecretStore>();

        // Register the Db-backed store (read/write, persisted via EF Core).
        // Optionally uses ISecretProtector for encrypted-at-rest storage.
        services.AddScoped<DbSecretStore>();

        // Register the resolver that dispatches by SecretReference.Kind.
        // Uses lazy resolution from IServiceProvider to support scoped stores
        // (e.g., DbSecretStore which depends on scoped DbContext).
        services.AddSingleton<IManagedSecretStore, SecretStoreResolver>();

        return services;
    }
}

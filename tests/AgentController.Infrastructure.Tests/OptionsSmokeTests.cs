using AgentController.Application;
using AgentController.Application.Results;
using AgentController.Domain;
using AgentController.Infrastructure;
using AgentController.Infrastructure.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace AgentController.Infrastructure.Tests;

public class OptionsSmokeTests
{
    private static IConfiguration BuildConfiguration(Dictionary<string, string?>? overrides = null)
    {
        // Base configuration matching the example JSON with all required fields
        var values = new Dictionary<string, string?>
        {
            ["agentController:workerId"] = "test-worker",
            ["agentController:pollIntervalSeconds"] = "30",
            ["agentController:maxConcurrentRuns"] = "3",
            ["agentController:runRoot"] = "/tmp/runs",
            ["persistence:provider"] = "Sqlite",
            ["persistence:connectionString"] = "Data Source=test.db",
            ["workSource:provider"] = "LocalFake",
            ["sourceControl:provider"] = "LocalFake",
            ["environmentProvider:provider"] = "LocalWorkspace",
            ["runtime:provider"] = "NoOp",
            ["repositories:example-service:cloneUrl"] = "https://example.com/repo.git",
        };

        if (overrides is not null)
        {
            foreach (var (key, value) in overrides)
            {
                values[key] = value;
            }
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    [Fact]
    public void AgentControllerOptions_BindsFromConfiguration()
    {
        var config = BuildConfiguration();
        var services = new ServiceCollection();
        services.AddOptions<AgentControllerOptions>().Bind(config.GetSection("agentController"));

        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<AgentControllerOptions>>().Value;

        Assert.Equal("test-worker", options.WorkerId);
        Assert.Equal(30, options.PollIntervalSeconds);
        Assert.Equal(3, options.MaxConcurrentRuns);
        Assert.Equal("/tmp/runs", options.RunRoot);
        Assert.True(options.RetainSuccessfulRuns);
        Assert.True(options.RetainFailedRuns);
    }

    [Fact]
    public void AgentControllerOptions_DefaultsRetainToTrue()
    {
        var values = new Dictionary<string, string?>
        {
            ["agentController:workerId"] = "test-worker",
            ["agentController:runRoot"] = "/tmp/runs",
        };

        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddOptions<AgentControllerOptions>().Bind(config.GetSection("agentController"));

        var options = services
            .BuildServiceProvider()
            .GetRequiredService<IOptions<AgentControllerOptions>>()
            .Value;

        Assert.True(options.RetainSuccessfulRuns);
        Assert.True(options.RetainFailedRuns);
    }

    [Fact]
    public void PersistenceOptions_BindsFromConfiguration()
    {
        var config = BuildConfiguration();
        var services = new ServiceCollection();
        services.AddOptions<PersistenceOptions>().Bind(config.GetSection("persistence"));

        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<PersistenceOptions>>().Value;

        Assert.Equal("Sqlite", options.Provider);
        Assert.Equal("Data Source=test.db", options.ConnectionString);
    }

    [Fact]
    public void WorkSourceOptions_BindsFromConfiguration()
    {
        var config = BuildConfiguration(
            new Dictionary<string, string?>
            {
                ["workSource:tagPrefix"] = "ac",
            }
        );

        var services = new ServiceCollection();
        services.AddOptions<WorkSourceOptions>().Bind(config.GetSection("workSource"));

        var options = services
            .BuildServiceProvider()
            .GetRequiredService<IOptions<WorkSourceOptions>>()
            .Value;

        Assert.Equal("LocalFake", options.Provider);
        Assert.Equal("ac", options.TagPrefix);
    }

    [Fact]
    public void WorkSourceOptions_DefaultTagPrefix_IsAgent()
    {
        var options = new WorkSourceOptions { Provider = "LocalFake" };

        Assert.Equal("agent", options.TagPrefix);
    }

    [Fact]
    public void WorkSourceOptions_PrefixAwareTagHelpers_DefaultPrefix()
    {
        // Prefix-aware helpers with default prefix produce the expected
        // controller-owned lifecycle tags.
        Assert.Equal("agent-ready", WorkSourceOptions.TagReady());
        Assert.Equal("agent-active", WorkSourceOptions.TagActive());
        Assert.Equal("agent-failed", WorkSourceOptions.TagFailed());
        Assert.Equal("agent-needs-human", WorkSourceOptions.TagNeedsHuman());

        var lifecycle = WorkSourceOptions.LifecycleTags();
        Assert.Equal(3, lifecycle.Count);
        Assert.Contains("agent-active", lifecycle);
        Assert.Contains("agent-failed", lifecycle);
        Assert.Contains("agent-needs-human", lifecycle);
    }

    [Fact]
    public void WorkSourceOptions_PrefixAwareTagHelpers_CustomPrefix()
    {
        // Custom prefix produces namespaced tags for collision avoidance.
        Assert.Equal("ac-ready", WorkSourceOptions.TagReady("ac"));
        Assert.Equal("ac-active", WorkSourceOptions.TagActive("ac"));
        Assert.Equal("ac-failed", WorkSourceOptions.TagFailed("ac"));
        Assert.Equal("ac-needs-human", WorkSourceOptions.TagNeedsHuman("ac"));

        var lifecycle = WorkSourceOptions.LifecycleTags("ac");
        Assert.Equal(3, lifecycle.Count);
        Assert.Contains("ac-active", lifecycle);
        Assert.Contains("ac-failed", lifecycle);
        Assert.Contains("ac-needs-human", lifecycle);
    }

    [Fact]
    public void WorkSourceOptions_LegacyConfigKeys_IgnoredGracefully()
    {
        // Unknown legacy keys (eligibleTags, excludedTags, eligibleStates,
        // workItemType) are simply ignored by the options binder after
        // the fields have been removed from the type.
        var config = BuildConfiguration(
            new Dictionary<string, string?>
            {
                ["workSource:eligibleTags:0"] = "agent-ready",
                ["workSource:excludedTags:0"] = "agent-blocked",
                ["workSource:eligibleStates:0"] = "New",
                ["workSource:workItemType"] = "Task",
            }
        );

        var services = new ServiceCollection();
        services.AddOptions<WorkSourceOptions>().Bind(config.GetSection("workSource"));

        var provider = services.BuildServiceProvider();
        // Should not throw — unknown keys are silently ignored.
        var options = provider.GetRequiredService<IOptions<WorkSourceOptions>>().Value;

        Assert.Equal("LocalFake", options.Provider);
        Assert.Equal("agent", options.TagPrefix);
    }

    [Fact]
    public void SourceControlOptions_BindsFromConfiguration()
    {
        var config = BuildConfiguration();
        var services = new ServiceCollection();
        services.AddOptions<SourceControlOptions>().Bind(config.GetSection("sourceControl"));

        var options = services
            .BuildServiceProvider()
            .GetRequiredService<IOptions<SourceControlOptions>>()
            .Value;

        Assert.Equal("LocalFake", options.Provider);
    }

    [Fact]
    public void EnvironmentProviderOptions_BindsFromConfiguration()
    {
        var config = BuildConfiguration();
        var services = new ServiceCollection();
        services
            .AddOptions<EnvironmentProviderOptions>()
            .Bind(config.GetSection("environmentProvider"));

        var options = services
            .BuildServiceProvider()
            .GetRequiredService<IOptions<EnvironmentProviderOptions>>()
            .Value;

        Assert.Equal("LocalWorkspace", options.Provider);
    }

    [Fact]
    public void RuntimeOptions_BindsFromConfiguration()
    {
        var config = BuildConfiguration(
            new Dictionary<string, string?>
            {
                ["runtime:piExecutablePath"] = "/usr/local/bin/pi",
            }
        );

        var services = new ServiceCollection();
        services.AddOptions<RuntimeOptions>().Bind(config.GetSection("runtime"));

        var options = services
            .BuildServiceProvider()
            .GetRequiredService<IOptions<RuntimeOptions>>()
            .Value;

        Assert.Equal("NoOp", options.Provider);
        Assert.Equal("/usr/local/bin/pi", options.PiExecutablePath);
    }

    [Fact]
    public void RepositoryProfileOptions_BindsFromConfiguration()
    {
        var config = BuildConfiguration(
            new Dictionary<string, string?>
            {
                ["repositories:example-service:allowedPaths:0"] = "src/",
                ["repositories:example-service:allowedPaths:1"] = "tests/",
            }
        );

        var services = new ServiceCollection();
        services
            .AddOptions<Dictionary<string, RepositoryProfileOptions>>()
            .Bind(config.GetSection("repositories"));

        var options = services
            .BuildServiceProvider()
            .GetRequiredService<IOptions<Dictionary<string, RepositoryProfileOptions>>>()
            .Value;

        Assert.NotNull(options);
        Assert.True(options.ContainsKey("example-service"));

        var profile = options["example-service"];
        Assert.Equal("https://example.com/repo.git", profile.CloneUrl);
        Assert.Equal("main", profile.DefaultBranch);
        Assert.Equal(2, profile.AllowedPaths.Count);
        Assert.Contains("src/", profile.AllowedPaths);
    }

    [Fact]
    public void AgentControllerOptions_ValidationCatchesNegativePollInterval()
    {
        var config = BuildConfiguration(
            new Dictionary<string, string?> { ["agentController:pollIntervalSeconds"] = "-5" }
        );

        var services = new ServiceCollection();
        services
            .AddOptions<AgentControllerOptions>()
            .Bind(config.GetSection("agentController"))
            .ValidateDataAnnotations();

        var provider = services.BuildServiceProvider();
        var ex = Assert.Throws<OptionsValidationException>(() =>
            provider.GetRequiredService<IOptions<AgentControllerOptions>>().Value
        );

        Assert.Contains("PollIntervalSeconds", ex.Message);
    }

    [Fact]
    public void AgentControllerOptions_ValidationCatchesZeroPollInterval()
    {
        var config = BuildConfiguration(
            new Dictionary<string, string?> { ["agentController:pollIntervalSeconds"] = "0" }
        );

        var services = new ServiceCollection();
        services
            .AddOptions<AgentControllerOptions>()
            .Bind(config.GetSection("agentController"))
            .ValidateDataAnnotations();

        var provider = services.BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(() =>
            provider.GetRequiredService<IOptions<AgentControllerOptions>>().Value
        );
    }

    [Fact]
    public void AgentControllerOptions_ValidationCatchesZeroMaxConcurrency()
    {
        var config = BuildConfiguration(
            new Dictionary<string, string?> { ["agentController:maxConcurrentRuns"] = "0" }
        );

        var services = new ServiceCollection();
        services
            .AddOptions<AgentControllerOptions>()
            .Bind(config.GetSection("agentController"))
            .ValidateDataAnnotations();

        var provider = services.BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(() =>
            provider.GetRequiredService<IOptions<AgentControllerOptions>>().Value
        );
    }

    [Fact]
    public void AgentControllerOptions_ValidationCatchesEmptyWorkerId()
    {
        var config = BuildConfiguration(
            new Dictionary<string, string?> { ["agentController:workerId"] = "" }
        );

        var services = new ServiceCollection();
        services
            .AddOptions<AgentControllerOptions>()
            .Bind(config.GetSection("agentController"))
            .ValidateDataAnnotations();

        var provider = services.BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(() =>
            provider.GetRequiredService<IOptions<AgentControllerOptions>>().Value
        );
    }

    [Fact]
    public void PersistenceOptions_ValidationCatchesEmptyProvider()
    {
        var config = BuildConfiguration(
            new Dictionary<string, string?> { ["persistence:provider"] = "" }
        );

        var services = new ServiceCollection();
        services
            .AddOptions<PersistenceOptions>()
            .Bind(config.GetSection("persistence"))
            .ValidateDataAnnotations();

        var provider = services.BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(() =>
            provider.GetRequiredService<IOptions<PersistenceOptions>>().Value
        );
    }

    [Fact]
    public void WorkSourceOptions_ValidationCatchesEmptyProvider()
    {
        var config = BuildConfiguration(
            new Dictionary<string, string?> { ["workSource:provider"] = "" }
        );

        var services = new ServiceCollection();
        services
            .AddOptions<WorkSourceOptions>()
            .Bind(config.GetSection("workSource"))
            .ValidateDataAnnotations();

        var provider = services.BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(() =>
            provider.GetRequiredService<IOptions<WorkSourceOptions>>().Value
        );
    }

    [Fact]
    public void SourceControlOptions_ValidationCatchesEmptyProvider()
    {
        var config = BuildConfiguration(
            new Dictionary<string, string?> { ["sourceControl:provider"] = "" }
        );

        var services = new ServiceCollection();
        services
            .AddOptions<SourceControlOptions>()
            .Bind(config.GetSection("sourceControl"))
            .ValidateDataAnnotations();

        var provider = services.BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(() =>
            provider.GetRequiredService<IOptions<SourceControlOptions>>().Value
        );
    }

    [Fact]
    public void EnvironmentProviderOptions_ValidationCatchesEmptyProvider()
    {
        var config = BuildConfiguration(
            new Dictionary<string, string?> { ["environmentProvider:provider"] = "" }
        );

        var services = new ServiceCollection();
        services
            .AddOptions<EnvironmentProviderOptions>()
            .Bind(config.GetSection("environmentProvider"))
            .ValidateDataAnnotations();

        var provider = services.BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(() =>
            provider.GetRequiredService<IOptions<EnvironmentProviderOptions>>().Value
        );
    }

    [Fact]
    public void RuntimeOptions_ValidationCatchesEmptyProvider()
    {
        var config = BuildConfiguration(
            new Dictionary<string, string?> { ["runtime:provider"] = "" }
        );

        var services = new ServiceCollection();
        services
            .AddOptions<RuntimeOptions>()
            .Bind(config.GetSection("runtime"))
            .ValidateDataAnnotations();

        var provider = services.BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(() =>
            provider.GetRequiredService<IOptions<RuntimeOptions>>().Value
        );
    }

    [Fact]
    public void ExampleJson_AllProviderNamesAreNonEmpty()
    {
        // Read appsettings.example.json and verify all provider fields are non-empty
        var examplePath = Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "..",
            "appsettings.example.json"
        );

        // If the file exists at the solution root, validate it
        if (File.Exists(examplePath))
        {
            var config = new ConfigurationBuilder().AddJsonFile(examplePath).Build();

            var sections = new[]
            {
                ("persistence:provider", "Persistence"),
                ("workSource:provider", "WorkSource"),
                ("sourceControl:provider", "SourceControl"),
                ("environmentProvider:provider", "EnvironmentProvider"),
                ("runtime:provider", "Runtime"),
            };

            foreach (var (key, name) in sections)
            {
                var value = config[key];
                Assert.False(
                    string.IsNullOrWhiteSpace(value),
                    $"{name} provider should not be empty in appsettings.example.json. Key: {key}"
                );
            }
        }
        // If file doesn't exist, skip (test is informational)
    }

    // ──────────────────────────────────────────────
    // Azure DevOps Boards Options tests
    // ──────────────────────────────────────────────

    [Fact]
    public void AzureDevOpsBoardsOptions_BindsFromConfiguration()
    {
        var values = new Dictionary<string, string?>
        {
            ["azureDevOps:personalAccessToken"] = "test-pat-token",
        };

        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services
            .AddOptions<AzureDevOpsBoardsOptions>()
            .Bind(config.GetSection("azureDevOps"));

        var options = services
            .BuildServiceProvider()
            .GetRequiredService<IOptions<AzureDevOpsBoardsOptions>>()
            .Value;

        Assert.Equal("test-pat-token", options.PersonalAccessToken);
    }

    [Fact]
    public void AzureDevOpsBoardsOptions_ResolvePat_ReturnsDirectValue()
    {
        var options = new AzureDevOpsBoardsOptions
        {
            PersonalAccessToken = "my-direct-pat",
        };

        var resolved = options.ResolvePersonalAccessToken();

        Assert.Equal("my-direct-pat", resolved);
    }

    [Fact]
    public void AzureDevOpsBoardsOptions_ResolvePat_ReturnsNullForEmptyValue()
    {
        var options = new AzureDevOpsBoardsOptions
        {
            PersonalAccessToken = "",
        };

        var resolved = options.ResolvePersonalAccessToken();

        Assert.Null(resolved);
    }

    [Fact]
    public void AzureDevOpsBoardsOptions_ResolvePat_ReturnsNullForWhitespaceValue()
    {
        var options = new AzureDevOpsBoardsOptions
        {
            PersonalAccessToken = "   ",
        };

        var resolved = options.ResolvePersonalAccessToken();

        Assert.Null(resolved);
    }

    [Fact]
    public void AzureDevOpsBoardsOptions_ValidationCatchesEmptyPat()
    {
        // Validation is deferred to AzureDevOpsBoardsValidator, not data annotations.
        var workSource = new WorkSourceOptions
        {
            Provider = "AzureDevOpsBoards",
            OrganizationUrl = "https://dev.azure.com/myorg",
            Project = "MyProject",
        };

        var boards = new AzureDevOpsBoardsOptions
        {
            PersonalAccessToken = "",
        };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            AzureDevOpsBoardsValidator.Validate(workSource, boards));

        Assert.Contains("PAT", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ──────────────────────────────────────────────
    // Azure DevOps Boards Validator tests
    // ──────────────────────────────────────────────

    [Fact]
    public void AzureDevOpsBoardsValidator_ValidConfig_Passes()
    {
        var workSource = new WorkSourceOptions
        {
            Provider = "AzureDevOpsBoards",
            OrganizationUrl = "https://dev.azure.com/myorg",
            Project = "MyProject",
        };

        var boards = new AzureDevOpsBoardsOptions
        {
            PersonalAccessToken = "test-pat",
        };

        // Should not throw
        var ex = Record.Exception(() =>
            AzureDevOpsBoardsValidator.Validate(workSource, boards));

        Assert.Null(ex);
    }

    [Fact]
    public void AzureDevOpsBoardsValidator_ThrowsWhenOrganizationUrlMissing()
    {
        var workSource = new WorkSourceOptions
        {
            Provider = "AzureDevOpsBoards",
            OrganizationUrl = null,
            Project = "MyProject",
        };

        var boards = new AzureDevOpsBoardsOptions
        {
            PersonalAccessToken = "test-pat",
        };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            AzureDevOpsBoardsValidator.Validate(workSource, boards));

        Assert.Contains("organization URL", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AzureDevOpsBoardsValidator_ThrowsWhenOrganizationUrlInvalid()
    {
        var workSource = new WorkSourceOptions
        {
            Provider = "AzureDevOpsBoards",
            OrganizationUrl = "not-a-valid-url",
            Project = "MyProject",
        };

        var boards = new AzureDevOpsBoardsOptions
        {
            PersonalAccessToken = "test-pat",
        };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            AzureDevOpsBoardsValidator.Validate(workSource, boards));

        Assert.Contains("not a valid", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AzureDevOpsBoardsValidator_ThrowsWhenProjectMissing()
    {
        var workSource = new WorkSourceOptions
        {
            Provider = "AzureDevOpsBoards",
            OrganizationUrl = "https://dev.azure.com/myorg",
            Project = null,
        };

        var boards = new AzureDevOpsBoardsOptions
        {
            PersonalAccessToken = "test-pat",
        };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            AzureDevOpsBoardsValidator.Validate(workSource, boards));

        Assert.Contains("project", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AzureDevOpsBoardsValidator_ThrowsWhenPatMissing()
    {
        var workSource = new WorkSourceOptions
        {
            Provider = "AzureDevOpsBoards",
            OrganizationUrl = "https://dev.azure.com/myorg",
            Project = "MyProject",
        };

        var boards = new AzureDevOpsBoardsOptions
        {
            PersonalAccessToken = "",
        };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            AzureDevOpsBoardsValidator.Validate(workSource, boards));

        Assert.Contains("PAT", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AzureDevOpsBoardsValidator_ReportsMultipleFailures()
    {
        var workSource = new WorkSourceOptions
        {
            Provider = "AzureDevOpsBoards",
            OrganizationUrl = null,
            Project = null,
        };

        var boards = new AzureDevOpsBoardsOptions
        {
            PersonalAccessToken = "",
        };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            AzureDevOpsBoardsValidator.Validate(workSource, boards));

        // Should report all failures (at least 3: org url, project, PAT)
        var message = ex.Message;
        Assert.Contains("1.", message);
        Assert.Contains("2.", message);
        Assert.Contains("3.", message);
    }

    // ──────────────────────────────────────────────
    // Repository profile cloneUrl validation tests
    // ──────────────────────────────────────────────

    [Fact]
    public void RepositoryProfiles_ValidationCatchesEmptyCloneUrl()
    {
        var config = BuildConfiguration(
            new Dictionary<string, string?>
            {
                ["repositories:example-service:cloneUrl"] = "",
            }
        );

        var services = new ServiceCollection();
        services.AddAgentControllerOptions(config);

        var provider = services.BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(() =>
            provider.GetRequiredService<IOptions<Dictionary<string, RepositoryProfileOptions>>>().Value
        );
    }

    [Fact]
    public void RepositoryProfiles_ValidationCatchesWhitespaceCloneUrl()
    {
        var config = BuildConfiguration(
            new Dictionary<string, string?>
            {
                ["repositories:example-service:cloneUrl"] = "   ",
            }
        );

        var services = new ServiceCollection();
        services.AddAgentControllerOptions(config);

        var provider = services.BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(() =>
            provider.GetRequiredService<IOptions<Dictionary<string, RepositoryProfileOptions>>>().Value
        );
    }

    [Fact]
    public void RepositoryProfiles_ValidationPassesForValidHttpsUrl()
    {
        var config = BuildConfiguration(
            new Dictionary<string, string?>
            {
                ["repositories:example-service:cloneUrl"] = "https://dev.azure.com/org/project/_git/repo",
            }
        );

        var services = new ServiceCollection();
        services.AddAgentControllerOptions(config);

        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<Dictionary<string, RepositoryProfileOptions>>>().Value;

        Assert.True(options.ContainsKey("example-service"));
        Assert.Equal("https://dev.azure.com/org/project/_git/repo", options["example-service"].CloneUrl);
    }

    [Fact]
    public void RepositoryProfiles_ValidationPassesForValidSshUrl()
    {
        var config = BuildConfiguration(
            new Dictionary<string, string?>
            {
                ["repositories:example-service:cloneUrl"] = "git@ssh.dev.azure.com:v3/org/project/repo",
            }
        );

        var services = new ServiceCollection();
        services.AddAgentControllerOptions(config);

        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<Dictionary<string, RepositoryProfileOptions>>>().Value;

        Assert.True(options.ContainsKey("example-service"));
        Assert.Equal("git@ssh.dev.azure.com:v3/org/project/repo", options["example-service"].CloneUrl);
    }

    [Fact]
    public void RepositoryProfiles_ValidationPassesForLocalPath()
    {
        var config = BuildConfiguration(
            new Dictionary<string, string?>
            {
                ["repositories:example-service:cloneUrl"] = "/home/user/projects/repo",
            }
        );

        var services = new ServiceCollection();
        services.AddAgentControllerOptions(config);

        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<Dictionary<string, RepositoryProfileOptions>>>().Value;

        Assert.True(options.ContainsKey("example-service"));
        Assert.Equal("/home/user/projects/repo", options["example-service"].CloneUrl);
    }

    [Fact]
    public void RepositoryProfiles_TransportBindsFromConfig_Ssh()
    {
        var config = BuildConfiguration(
            new Dictionary<string, string?>
            {
                ["repositories:example-service:cloneUrl"] = "git@ssh.dev.azure.com:v3/org/project/repo",
                ["repositories:example-service:transport"] = "Ssh",
            }
        );

        var services = new ServiceCollection();
        services.AddAgentControllerOptions(config);

        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<Dictionary<string, RepositoryProfileOptions>>>().Value;

        Assert.Equal(CloneTransport.Ssh, options["example-service"].Transport);
    }

    [Fact]
    public void RepositoryProfiles_TransportBindsFromConfig_HttpsPat()
    {
        var config = BuildConfiguration(
            new Dictionary<string, string?>
            {
                ["repositories:example-service:cloneUrl"] = "https://dev.azure.com/org/project/_git/repo",
                ["repositories:example-service:transport"] = "HttpsPat",
            }
        );

        var services = new ServiceCollection();
        services.AddAgentControllerOptions(config);

        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<Dictionary<string, RepositoryProfileOptions>>>().Value;

        Assert.Equal(CloneTransport.HttpsPat, options["example-service"].Transport);
    }

    [Fact]
    public void RepositoryProfiles_TransportDefaultsToUnspecified()
    {
        var config = BuildConfiguration(
            new Dictionary<string, string?>
            {
                ["repositories:example-service:cloneUrl"] = "https://example.com/repo",
            }
        );

        var services = new ServiceCollection();
        services.AddAgentControllerOptions(config);

        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<Dictionary<string, RepositoryProfileOptions>>>().Value;

        Assert.Equal(CloneTransport.Unspecified, options["example-service"].Transport);
    }

    [Fact]
    public void ExampleJson_PollIntervalAndMaxConcurrencyArePositive()
    {
        var examplePath = Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "..",
            "appsettings.example.json"
        );

        if (File.Exists(examplePath))
        {
            var config = new ConfigurationBuilder().AddJsonFile(examplePath).Build();

            var pollInterval = config.GetValue<int>("agentController:pollIntervalSeconds");
            var maxConcurrency = config.GetValue<int>("agentController:maxConcurrentRuns");

            Assert.True(
                pollInterval > 0,
                $"pollIntervalSeconds should be positive, got {pollInterval}"
            );
            Assert.True(
                maxConcurrency > 0,
                $"maxConcurrentRuns should be positive, got {maxConcurrency}"
            );
        }
    }

    // ──────────────────────────────────────────────
    // AzureDevOpsPatResolver tests
    // ──────────────────────────────────────────────

    [Fact]
    public async Task AzureDevOpsPatResolver_ResolveFromLegacyValue_ReturnsDirectValue()
    {
        var result = await AzureDevOpsPatResolver.ResolveFromLegacyValueAsync(
            "my-direct-pat-token",
            CancellationToken.None
        );

        Assert.Equal("my-direct-pat-token", result);
    }

    [Fact]
    public async Task AzureDevOpsPatResolver_ResolveFromLegacyValue_ReturnsNullForEmptyValue()
    {
        var result = await AzureDevOpsPatResolver.ResolveFromLegacyValueAsync(
            "",
            CancellationToken.None
        );

        Assert.Null(result);
    }

    [Fact]
    public async Task AzureDevOpsPatResolver_ResolveFromLegacyValue_ReturnsValueAsIsForEnvPrefix()
    {
        // ENV: prefix is no longer parsed — returned as-is (treated as a direct PAT value).
        var result = await AzureDevOpsPatResolver.ResolveFromLegacyValueAsync(
            "ENV:NONEXISTENT_VAR_XYZ",
            CancellationToken.None
        );

        // The string is returned as-is since ENV: parsing was removed.
        Assert.Equal("ENV:NONEXISTENT_VAR_XYZ", result);
    }

    // ──────────────────────────────────────────────
    // AzureDevOpsBoardsOptions async resolution tests
    // ──────────────────────────────────────────────

    [Fact]
    public async Task AzureDevOpsBoardsOptions_ResolvePatAsync_ReturnsDirectValue()
    {
        var options = new AzureDevOpsBoardsOptions
        {
            PersonalAccessToken = "my-direct-pat",
        };

        var resolved = await options.ResolvePersonalAccessTokenAsync(
            new InMemoryFakeSecretStore(new Dictionary<string, string>()),
            CancellationToken.None
        );

        Assert.Equal("my-direct-pat", resolved);
    }

    [Fact]
    public async Task AzureDevOpsBoardsOptions_ResolvePatAsync_ReturnsNullForEmptyValue()
    {
        var options = new AzureDevOpsBoardsOptions
        {
            PersonalAccessToken = "",
        };

        var resolved = await options.ResolvePersonalAccessTokenAsync(
            new InMemoryFakeSecretStore(new Dictionary<string, string>()),
            CancellationToken.None
        );

        Assert.Null(resolved);
    }

    // ──────────────────────────────────────────────
    // Fake secret store implementations for tests
    // ──────────────────────────────────────────────

    private sealed class InMemoryFakeSecretStore(Dictionary<string, string> secrets)
        : IManagedSecretStore
    {
        public Task<string?> ResolveAsync(SecretReference reference, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            secrets.TryGetValue($"{reference.Kind}:{reference.Id}", out var value);
            return Task.FromResult(value);
        }

        public Task<SecretWriteResult> WriteAsync(
            SecretReference reference,
            string value,
            CancellationToken ct
        )
        {
            ct.ThrowIfCancellationRequested();
            secrets[$"{reference.Kind}:{reference.Id}"] = value;
            return Task.FromResult(SecretWriteResult.SuccessResult());
        }
    }
}

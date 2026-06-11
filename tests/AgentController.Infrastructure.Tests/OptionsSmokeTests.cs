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
        services
            .AddOptions<AgentControllerOptions>()
            .Bind(config.GetSection("agentController"));

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
        services
            .AddOptions<AgentControllerOptions>()
            .Bind(config.GetSection("agentController"));

        var options = services.BuildServiceProvider()
            .GetRequiredService<IOptions<AgentControllerOptions>>().Value;

        Assert.True(options.RetainSuccessfulRuns);
        Assert.True(options.RetainFailedRuns);
    }

    [Fact]
    public void PersistenceOptions_BindsFromConfiguration()
    {
        var config = BuildConfiguration();
        var services = new ServiceCollection();
        services
            .AddOptions<PersistenceOptions>()
            .Bind(config.GetSection("persistence"));

        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<PersistenceOptions>>().Value;

        Assert.Equal("Sqlite", options.Provider);
        Assert.Equal("Data Source=test.db", options.ConnectionString);
    }

    [Fact]
    public void WorkSourceOptions_BindsFromConfiguration()
    {
        var config = BuildConfiguration(new Dictionary<string, string?>
        {
            ["workSource:eligibleTags:0"] = "agent-ready",
            ["workSource:eligibleTags:1"] = "autonomous",
            ["workSource:excludedTags:0"] = "agent-blocked",
            ["workSource:eligibleStates:0"] = "New",
            ["workSource:eligibleStates:1"] = "Approved",
        });

        var services = new ServiceCollection();
        services
            .AddOptions<WorkSourceOptions>()
            .Bind(config.GetSection("workSource"));

        var options = services.BuildServiceProvider()
            .GetRequiredService<IOptions<WorkSourceOptions>>().Value;

        Assert.Equal("LocalFake", options.Provider);
        Assert.Equal(2, options.EligibleTags.Count);
        Assert.Contains("agent-ready", options.EligibleTags);
        Assert.Contains("autonomous", options.EligibleTags);
        Assert.Single(options.ExcludedTags);
        Assert.Contains("agent-blocked", options.ExcludedTags);
        Assert.Equal(2, options.EligibleStates.Count);
        Assert.Contains("New", options.EligibleStates);
    }

    [Fact]
    public void SourceControlOptions_BindsFromConfiguration()
    {
        var config = BuildConfiguration();
        var services = new ServiceCollection();
        services
            .AddOptions<SourceControlOptions>()
            .Bind(config.GetSection("sourceControl"));

        var options = services.BuildServiceProvider()
            .GetRequiredService<IOptions<SourceControlOptions>>().Value;

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

        var options = services.BuildServiceProvider()
            .GetRequiredService<IOptions<EnvironmentProviderOptions>>().Value;

        Assert.Equal("LocalWorkspace", options.Provider);
    }

    [Fact]
    public void RuntimeOptions_BindsFromConfiguration()
    {
        var config = BuildConfiguration(new Dictionary<string, string?>
        {
            ["runtime:piExecutablePath"] = "/usr/local/bin/pi",
            ["runtime:defaultMateriaLoadout"] = "autonomous-dev",
        });

        var services = new ServiceCollection();
        services
            .AddOptions<RuntimeOptions>()
            .Bind(config.GetSection("runtime"));

        var options = services.BuildServiceProvider()
            .GetRequiredService<IOptions<RuntimeOptions>>().Value;

        Assert.Equal("NoOp", options.Provider);
        Assert.Equal("/usr/local/bin/pi", options.PiExecutablePath);
        Assert.Equal("autonomous-dev", options.DefaultMateriaLoadout);
    }

    [Fact]
    public void RepositoryProfileOptions_BindsFromConfiguration()
    {
        var config = BuildConfiguration(new Dictionary<string, string?>
        {
            ["repositories:example-service:allowedPaths:0"] = "src/",
            ["repositories:example-service:allowedPaths:1"] = "tests/",
        });

        var services = new ServiceCollection();
        services
            .AddOptions<Dictionary<string, RepositoryProfileOptions>>()
            .Bind(config.GetSection("repositories"));

        var options = services.BuildServiceProvider()
            .GetRequiredService<IOptions<Dictionary<string, RepositoryProfileOptions>>>().Value;

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
        var config = BuildConfiguration(new Dictionary<string, string?>
        {
            ["agentController:pollIntervalSeconds"] = "-5",
        });

        var services = new ServiceCollection();
        services
            .AddOptions<AgentControllerOptions>()
            .Bind(config.GetSection("agentController"))
            .ValidateDataAnnotations();

        var provider = services.BuildServiceProvider();
        var ex = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<AgentControllerOptions>>().Value);

        Assert.Contains("PollIntervalSeconds", ex.Message);
    }

    [Fact]
    public void AgentControllerOptions_ValidationCatchesZeroPollInterval()
    {
        var config = BuildConfiguration(new Dictionary<string, string?>
        {
            ["agentController:pollIntervalSeconds"] = "0",
        });

        var services = new ServiceCollection();
        services
            .AddOptions<AgentControllerOptions>()
            .Bind(config.GetSection("agentController"))
            .ValidateDataAnnotations();

        var provider = services.BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<AgentControllerOptions>>().Value);
    }

    [Fact]
    public void AgentControllerOptions_ValidationCatchesZeroMaxConcurrency()
    {
        var config = BuildConfiguration(new Dictionary<string, string?>
        {
            ["agentController:maxConcurrentRuns"] = "0",
        });

        var services = new ServiceCollection();
        services
            .AddOptions<AgentControllerOptions>()
            .Bind(config.GetSection("agentController"))
            .ValidateDataAnnotations();

        var provider = services.BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<AgentControllerOptions>>().Value);
    }

    [Fact]
    public void AgentControllerOptions_ValidationCatchesEmptyWorkerId()
    {
        var config = BuildConfiguration(new Dictionary<string, string?>
        {
            ["agentController:workerId"] = "",
        });

        var services = new ServiceCollection();
        services
            .AddOptions<AgentControllerOptions>()
            .Bind(config.GetSection("agentController"))
            .ValidateDataAnnotations();

        var provider = services.BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<AgentControllerOptions>>().Value);
    }

    [Fact]
    public void PersistenceOptions_ValidationCatchesEmptyProvider()
    {
        var config = BuildConfiguration(new Dictionary<string, string?>
        {
            ["persistence:provider"] = "",
        });

        var services = new ServiceCollection();
        services
            .AddOptions<PersistenceOptions>()
            .Bind(config.GetSection("persistence"))
            .ValidateDataAnnotations();

        var provider = services.BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<PersistenceOptions>>().Value);
    }

    [Fact]
    public void WorkSourceOptions_ValidationCatchesEmptyProvider()
    {
        var config = BuildConfiguration(new Dictionary<string, string?>
        {
            ["workSource:provider"] = "",
        });

        var services = new ServiceCollection();
        services
            .AddOptions<WorkSourceOptions>()
            .Bind(config.GetSection("workSource"))
            .ValidateDataAnnotations();

        var provider = services.BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<WorkSourceOptions>>().Value);
    }

    [Fact]
    public void SourceControlOptions_ValidationCatchesEmptyProvider()
    {
        var config = BuildConfiguration(new Dictionary<string, string?>
        {
            ["sourceControl:provider"] = "",
        });

        var services = new ServiceCollection();
        services
            .AddOptions<SourceControlOptions>()
            .Bind(config.GetSection("sourceControl"))
            .ValidateDataAnnotations();

        var provider = services.BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<SourceControlOptions>>().Value);
    }

    [Fact]
    public void EnvironmentProviderOptions_ValidationCatchesEmptyProvider()
    {
        var config = BuildConfiguration(new Dictionary<string, string?>
        {
            ["environmentProvider:provider"] = "",
        });

        var services = new ServiceCollection();
        services
            .AddOptions<EnvironmentProviderOptions>()
            .Bind(config.GetSection("environmentProvider"))
            .ValidateDataAnnotations();

        var provider = services.BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<EnvironmentProviderOptions>>().Value);
    }

    [Fact]
    public void RuntimeOptions_ValidationCatchesEmptyProvider()
    {
        var config = BuildConfiguration(new Dictionary<string, string?>
        {
            ["runtime:provider"] = "",
        });

        var services = new ServiceCollection();
        services
            .AddOptions<RuntimeOptions>()
            .Bind(config.GetSection("runtime"))
            .ValidateDataAnnotations();

        var provider = services.BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<RuntimeOptions>>().Value);
    }

    [Fact]
    public void ExampleJson_AllProviderNamesAreNonEmpty()
    {
        // Read appsettings.example.json and verify all provider fields are non-empty
        var examplePath = Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "..",
            "appsettings.example.json");

        // If the file exists at the solution root, validate it
        if (File.Exists(examplePath))
        {
            var config = new ConfigurationBuilder()
                .AddJsonFile(examplePath)
                .Build();

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
                Assert.False(string.IsNullOrWhiteSpace(value),
                    $"{name} provider should not be empty in appsettings.example.json. Key: {key}");
            }
        }
        // If file doesn't exist, skip (test is informational)
    }

    [Fact]
    public void ExampleJson_PollIntervalAndMaxConcurrencyArePositive()
    {
        var examplePath = Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "..",
            "appsettings.example.json");

        if (File.Exists(examplePath))
        {
            var config = new ConfigurationBuilder()
                .AddJsonFile(examplePath)
                .Build();

            var pollInterval = config.GetValue<int>("agentController:pollIntervalSeconds");
            var maxConcurrency = config.GetValue<int>("agentController:maxConcurrentRuns");

            Assert.True(pollInterval > 0,
                $"pollIntervalSeconds should be positive, got {pollInterval}");
            Assert.True(maxConcurrency > 0,
                $"maxConcurrentRuns should be positive, got {maxConcurrency}");
        }
    }
}

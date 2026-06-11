using AgentController.Application;
using AgentController.Domain;
using AgentController.Infrastructure;

namespace AgentController.Infrastructure.Tests;

public class InfrastructureSmokeTests
{
    [Fact]
    public void InfrastructureLayer_ReferencesApplicationAndDomain()
    {
        // Prove Infrastructure -> Application and Domain dependencies are resolvable.
        var appType = typeof(IWorkSource);
        Assert.NotNull(appType);

        var domainType = typeof(WorkCandidate);
        Assert.NotNull(domainType);
    }

    [Fact]
    public void NoOpWorkSource_ImplementsInterface()
    {
        var provider = new NoOpWorkSource();
        Assert.IsAssignableFrom<IWorkSource>(provider);
    }

    [Fact]
    public void NoOpSourceControlProvider_ImplementsInterface()
    {
        var provider = new NoOpSourceControlProvider();
        Assert.IsAssignableFrom<ISourceControlProvider>(provider);
    }

    [Fact]
    public void NoOpEnvironmentProvider_ImplementsInterface()
    {
        var provider = new NoOpEnvironmentProvider();
        Assert.IsAssignableFrom<IEnvironmentProvider>(provider);
    }

    [Fact]
    public void NoOpAgentRuntime_ImplementsInterface()
    {
        var provider = new NoOpAgentRuntime();
        Assert.IsAssignableFrom<IAgentRuntime>(provider);
    }

    [Fact]
    public async Task NoOpWorkSource_FindEligibleAsync_ReturnsEmptyList()
    {
        var source = new NoOpWorkSource();
        var candidates = await source.FindEligibleAsync(new WorkQuery(), CancellationToken.None);

        Assert.NotNull(candidates);
        Assert.Empty(candidates);
    }

    [Fact]
    public async Task NoOpWorkSource_TryClaimAsync_ReturnsFailed()
    {
        var source = new NoOpWorkSource();
        var candidate = new WorkCandidate { Id = "c1", ExternalId = "1", RepoKey = "r", Title = "t", Source = "f" };
        var claim = new ClaimRequest { WorkerId = "w1" };

        var result = await source.TryClaimAsync(candidate, claim, CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotNull(result.FailureReason);
        Assert.Null(result.WorkRef);
        Assert.Null(result.LeaseToken);
    }

    [Fact]
    public async Task NoOpSourceControlProvider_CloneAsync_IsDeterministic()
    {
        var provider = new NoOpSourceControlProvider();
        var spec = new RepositorySpec { RepoKey = "example", CloneUrl = "https://example.com/repo" };
        var env = new EnvironmentHandle { Id = "env-1" };

        var checkout = await provider.CloneAsync(spec, env, CancellationToken.None);

        Assert.Equal("example", checkout.RepoKey);
        Assert.Equal(DateTimeOffset.UnixEpoch, checkout.ClonedAt);
        Assert.Null(checkout.CommitSha);
        Assert.Equal(string.Empty, checkout.LocalPath);
    }

    [Fact]
    public async Task NoOpSourceControlProvider_GetStatusAsync_ReturnsNotExists()
    {
        var provider = new NoOpSourceControlProvider();
        var scRef = new SourceControlRef { Provider = "fake", RepoKey = "r" };

        var status = await provider.GetStatusAsync(scRef, CancellationToken.None);

        Assert.False(status.Exists);
        Assert.Null(status.PullRequestUrl);
        Assert.Null(status.PullRequestStatus);
    }

    [Fact]
    public async Task NoOpAgentRuntime_StartAsync_IsDeterministic()
    {
        var runtime = new NoOpAgentRuntime();
        var spec = new AgentRunSpec { RunId = "run-1" };

        var handle = await runtime.StartAsync(spec, CancellationToken.None);

        Assert.Equal("run-1", handle.RunId);
        Assert.Equal(RunLifecycleState.Queued, handle.Status);
        Assert.Equal(DateTimeOffset.UnixEpoch, handle.StartedAt);
        Assert.Null(handle.RuntimeRunId);
    }

    [Fact]
    public async Task NoOpAgentRuntime_GetStatusAsync_ReflectsHandle()
    {
        var runtime = new NoOpAgentRuntime();
        var handle = new AgentRunHandle { RunId = "run-1", Status = RunLifecycleState.Queued };

        var status = await runtime.GetStatusAsync(handle, CancellationToken.None);

        Assert.Equal(RunLifecycleState.Queued, status.Status);
        Assert.Equal("run-1", handle.RunId);
        Assert.Null(status.Error);
        Assert.Null(status.Events);
    }

    [Fact]
    public async Task NoOpEnvironmentProvider_CreateAsync_ReturnsHandle()
    {
        var provider = new NoOpEnvironmentProvider();
        var spec = new EnvironmentSpec { RunId = "run-1", Profile = "default" };

        var handle = await provider.CreateAsync(spec, CancellationToken.None);

        Assert.NotNull(handle);
        Assert.Contains("run-1", handle.Id);
        Assert.Equal("NoOp", handle.ProviderType);
    }

    [Fact]
    public async Task NoOpEnvironmentProvider_ExecuteAsync_ReturnsSuccess()
    {
        var provider = new NoOpEnvironmentProvider();
        var handle = new EnvironmentHandle { Id = "env-1" };
        var cmd = new CommandSpec { Command = "dotnet", Arguments = new List<string> { "build" } };

        var result = await provider.ExecuteAsync(handle, cmd, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.Equal(TimeSpan.Zero, result.Duration);
    }
}

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
}

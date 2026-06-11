using AgentController.Application;

namespace AgentController.Application.Tests;

public class ApplicationSmokeTests
{
    [Fact]
    public void ApplicationLayer_ReferencesDomain()
    {
        // Prove Application -> Domain dependency is resolvable.
        var domainType = typeof(Domain.WorkCandidate);
        Assert.NotNull(domainType);
    }

    [Fact]
    public void IWorkSource_IsDefined()
    {
        var type = typeof(IWorkSource);
        Assert.True(type.IsInterface);
    }

    [Fact]
    public void ISourceControlProvider_IsDefined()
    {
        var type = typeof(ISourceControlProvider);
        Assert.True(type.IsInterface);
    }

    [Fact]
    public void IEnvironmentProvider_IsDefined()
    {
        var type = typeof(IEnvironmentProvider);
        Assert.True(type.IsInterface);
    }

    [Fact]
    public void IAgentRuntime_IsDefined()
    {
        var type = typeof(IAgentRuntime);
        Assert.True(type.IsInterface);
    }
}

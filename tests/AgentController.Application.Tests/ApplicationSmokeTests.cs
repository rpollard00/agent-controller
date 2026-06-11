namespace AgentController.Application.Tests;

public class ApplicationSmokeTests
{
    [Fact]
    public void ApplicationPlaceholder_HasExpectedName()
    {
        Assert.Equal("AgentController.Application", ApplicationPlaceholder.Name);
    }

    [Fact]
    public void ApplicationLayer_ReferencesDomain()
    {
        // Prove Application -> Domain dependency is resolvable.
        var domainType = typeof(Domain.DomainPlaceholder);
        Assert.NotNull(domainType);
    }
}

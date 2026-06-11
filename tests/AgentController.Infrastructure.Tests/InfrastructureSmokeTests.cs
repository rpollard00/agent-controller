namespace AgentController.Infrastructure.Tests;

public class InfrastructureSmokeTests
{
    [Fact]
    public void InfrastructurePlaceholder_HasExpectedName()
    {
        Assert.Equal("AgentController.Infrastructure", InfrastructurePlaceholder.Name);
    }

    [Fact]
    public void InfrastructureLayer_ReferencesApplicationAndDomain()
    {
        // Prove Infrastructure -> Application and Domain dependencies are resolvable.
        var appType = typeof(Application.ApplicationPlaceholder);
        Assert.NotNull(appType);

        var domainType = typeof(Domain.DomainPlaceholder);
        Assert.NotNull(domainType);
    }
}

namespace AgentController.Domain.Tests;

public class DomainSmokeTests
{
    [Fact]
    public void DomainPlaceholder_HasExpectedName()
    {
        Assert.Equal("AgentController.Domain", DomainPlaceholder.Name);
    }

    [Fact]
    public void DomainProject_BuildsWithoutWarnings()
    {
        // Prove the project assemblies load and types are resolvable.
        var type = typeof(DomainPlaceholder);
        Assert.NotNull(type);
        Assert.Equal("AgentController.Domain", type.Namespace);
    }
}

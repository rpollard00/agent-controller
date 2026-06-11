using Microsoft.AspNetCore.Builder;

namespace AgentController.Api.Tests;

public class ApiSmokeTests
{
    [Fact]
    public void ApiLayer_ReferencesApplicationAndInfrastructure()
    {
        // Prove Api -> Application and Api -> Infrastructure dependencies are resolvable.
        var appType = typeof(Application.ApplicationPlaceholder);
        Assert.NotNull(appType);

        var infraType = typeof(Infrastructure.InfrastructurePlaceholder);
        Assert.NotNull(infraType);
    }

    [Fact]
    public void ApiHost_CanBuildServiceCollection()
    {
        // Prove the host builder doesn't throw on basic construction.
        var builder = WebApplication.CreateBuilder(Array.Empty<string>());
        var app = builder.Build();

        Assert.NotNull(app);
    }
}

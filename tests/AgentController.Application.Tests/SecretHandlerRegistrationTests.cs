using AgentController.Application.Abstractions;
using AgentController.Application.Commands;
using AgentController.Application.Results;
using Microsoft.Extensions.DependencyInjection;

namespace AgentController.Application.Tests;

public sealed class SecretHandlerRegistrationTests
{
    [Fact]
    public void AddApplicationHandlers_RegistersSecretCommandAndQueryHandlers()
    {
        var services = new ServiceCollection();

        services.AddApplicationHandlers();

        AssertRegistration<
            ICommandHandler<CreateSecretCommand, CreateSecretResult>,
            CreateSecretCommandHandler
        >(services);
        AssertRegistration<
            ICommandHandler<CreateSecretVersionCommand, CreateSecretVersionResult>,
            CreateSecretVersionCommandHandler
        >(services);
        AssertRegistration<
            ICommandHandler<DeleteSecretCommand, DeleteSecretResult>,
            DeleteSecretCommandHandler
        >(services);
    }

    private static void AssertRegistration<TService, TImplementation>(IServiceCollection services)
    {
        Assert.Contains(
            services,
            descriptor =>
                descriptor.ServiceType == typeof(TService)
                && descriptor.ImplementationType == typeof(TImplementation)
                && descriptor.Lifetime == ServiceLifetime.Scoped
        );
    }
}

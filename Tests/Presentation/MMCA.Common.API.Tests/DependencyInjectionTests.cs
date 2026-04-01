using AwesomeAssertions;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Microsoft.FeatureManagement.Mvc;
using MMCA.Common.API.Authorization;
using MMCA.Common.API.FeatureManagement;
using MMCA.Common.API.Idempotency;
using MMCA.Common.API.Middleware;
using MMCA.Common.Application.Modules;
using Moq;

namespace MMCA.Common.API.Tests;

public sealed class DependencyInjectionTests
{
    [Fact]
    public void AddAPI_RegistersIdempotencyFilter_AsScoped()
    {
        var services = new ServiceCollection();

        services.AddAPI();

        services.Any(s => s.ServiceType.Equals(typeof(IdempotencyFilter))
                       && s.Lifetime == ServiceLifetime.Scoped)
            .Should().BeTrue();
    }

    [Fact]
    public void AddAPI_RegistersOwnerOrAdminFilter_AsScoped()
    {
        var services = new ServiceCollection();

        services.AddAPI();

        services.Any(s => s.ServiceType.Equals(typeof(OwnerOrAdminFilter))
                       && s.Lifetime == ServiceLifetime.Scoped)
            .Should().BeTrue();
    }

    [Fact]
    public void AddAPI_RegistersDisabledFeatureHandler_AsSingleton()
    {
        var services = new ServiceCollection();

        services.AddAPI();

        services.Any(s => s.ServiceType.Equals(typeof(IDisabledFeaturesHandler))
                       && typeof(DisabledFeatureHandler).Equals(s.ImplementationType)
                       && s.Lifetime == ServiceLifetime.Singleton)
            .Should().BeTrue();
    }

    [Fact]
    public void AddAPI_WithConfiguration_BindsIdempotencySettings()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Idempotency:CacheExpirationHours"] = "48",
            })
            .Build();

        services.AddAPI(configuration: configuration);

        ServiceProvider provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<IdempotencySettings>>();
        options.Value.CacheExpirationHours.Should().Be(48);
    }

    [Fact]
    public void AddAPI_WithoutConfiguration_DoesNotRegisterIdempotencyOptions()
    {
        var services = new ServiceCollection();

        services.AddAPI();

        services.Any(s => s.ServiceType.Equals(typeof(IConfigureOptions<IdempotencySettings>)))
            .Should().BeFalse();
    }

    [Fact]
    public void AddAPI_ReturnsSameServiceCollection()
    {
        var services = new ServiceCollection();

        IServiceCollection result = services.AddAPI();

        result.Should().BeSameAs(services);
    }

    [Fact]
    public void AddCommonExceptionHandlers_RegistersAllFiveHandlers()
    {
        var services = new ServiceCollection();

        services.AddCommonExceptionHandlers();

        var handlerDescriptors = services
            .Where(s => s.ServiceType.Equals(typeof(IExceptionHandler)))
            .ToList();

        handlerDescriptors.Should().HaveCount(5);

        var implementationTypes = handlerDescriptors
            .Select(s => s.ImplementationType)
            .ToList();

        implementationTypes.Should().Contain(typeof(OperationCanceledExceptionHandler));
        implementationTypes.Should().Contain(typeof(DomainExceptionHandler));
        implementationTypes.Should().Contain(typeof(DbUpdateExceptionHandler));
        implementationTypes.Should().Contain(typeof(ValidationExceptionHandler));
        implementationTypes.Should().Contain(typeof(GlobalExceptionHandler));
    }

    [Fact]
    public void AddCommonExceptionHandlers_RegistersProblemDetails()
    {
        var services = new ServiceCollection();

        services.AddCommonExceptionHandlers();

        services.Any(s => s.ServiceType.Equals(typeof(IProblemDetailsService)))
            .Should().BeTrue();
    }

    [Fact]
    public void AddCommonExceptionHandlers_ReturnsSameServiceCollection()
    {
        var services = new ServiceCollection();

        IServiceCollection result = services.AddCommonExceptionHandlers();

        result.Should().BeSameAs(services);
    }

    [Fact]
    public void AddModuleHealthChecks_WithEnabledModules_RegistersHealthyChecks()
    {
        var services = new ServiceCollection();
        ModuleLoader moduleLoader = CreateModuleLoaderWithModules(
            enabledModuleNames: ["Catalog", "Sales"],
            disabledModuleNames: []);

        services.AddModuleHealthChecks(moduleLoader);

        ServiceProvider provider = services.BuildServiceProvider();
        var registrations = provider
            .GetRequiredService<IOptions<HealthCheckServiceOptions>>()
            .Value
            .Registrations;

        registrations.Should().Contain(r => r.Name == "module-Catalog");
        registrations.Should().Contain(r => r.Name == "module-Sales");
    }

    [Fact]
    public void AddModuleHealthChecks_WithDisabledModules_RegistersDegradedChecks()
    {
        var services = new ServiceCollection();
        ModuleLoader moduleLoader = CreateModuleLoaderWithModules(
            enabledModuleNames: [],
            disabledModuleNames: ["Identity"]);

        services.AddModuleHealthChecks(moduleLoader);

        ServiceProvider provider = services.BuildServiceProvider();
        var registrations = provider
            .GetRequiredService<IOptions<HealthCheckServiceOptions>>()
            .Value
            .Registrations;

        registrations.Should().Contain(r => r.Name == "module-Identity");
    }

    [Fact]
    public void AddModuleHealthChecks_WithMixedModules_RegistersAllChecks()
    {
        var services = new ServiceCollection();
        ModuleLoader moduleLoader = CreateModuleLoaderWithModules(
            enabledModuleNames: ["Catalog"],
            disabledModuleNames: ["Identity"]);

        services.AddModuleHealthChecks(moduleLoader);

        ServiceProvider provider = services.BuildServiceProvider();
        var registrations = provider
            .GetRequiredService<IOptions<HealthCheckServiceOptions>>()
            .Value
            .Registrations;

        registrations.Should().HaveCount(2);
        registrations.Should().Contain(r => r.Name == "module-Catalog");
        registrations.Should().Contain(r => r.Name == "module-Identity");
    }

    [Fact]
    public void AddModuleHealthChecks_AllChecksTaggedWithModule()
    {
        var services = new ServiceCollection();
        ModuleLoader moduleLoader = CreateModuleLoaderWithModules(
            enabledModuleNames: ["Catalog"],
            disabledModuleNames: ["Identity"]);

        services.AddModuleHealthChecks(moduleLoader);

        ServiceProvider provider = services.BuildServiceProvider();
        var registrations = provider
            .GetRequiredService<IOptions<HealthCheckServiceOptions>>()
            .Value
            .Registrations;

        registrations.Should().AllSatisfy(r => r.Tags.Should().Contain("module"));
    }

    [Fact]
    public void AddModuleHealthChecks_ReturnsSameServiceCollection()
    {
        var services = new ServiceCollection();
        ModuleLoader moduleLoader = CreateModuleLoaderWithModules([], []);

        IServiceCollection result = services.AddModuleHealthChecks(moduleLoader);

        result.Should().BeSameAs(services);
    }

    private static ModuleLoader CreateModuleLoaderWithModules(
        string[] enabledModuleNames,
        string[] disabledModuleNames)
    {
        var loader = new ModuleLoader();

        var enabledModulesField = typeof(ModuleLoader)
            .GetField("_enabledModules", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var disabledNamesField = typeof(ModuleLoader)
            .GetField("_disabledModuleNames", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        var enabledModules = (List<IModule>)enabledModulesField.GetValue(loader)!;
        foreach (string name in enabledModuleNames)
        {
            var moduleMock = new Mock<IModule>();
            moduleMock.Setup(m => m.Name).Returns(name);
            enabledModules.Add(moduleMock.Object);
        }

        var disabledNames = (List<string>)disabledNamesField.GetValue(loader)!;
        disabledNames.AddRange(disabledModuleNames);

        return loader;
    }
}

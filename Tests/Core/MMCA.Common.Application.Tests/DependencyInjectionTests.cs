using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Services;
using MMCA.Common.Application.Services.Query;
using MMCA.Common.Application.Settings;

namespace MMCA.Common.Application.Tests;

public sealed class DependencyInjectionTests
{
    private static ServiceCollection CreateServiceCollection()
    {
        var services = new ServiceCollection();
        services.AddApplication();
        return services;
    }

    // ── AddApplication: core service registrations ──
    [Fact]
    public void AddApplication_RegistersDomainEventDispatcher_AsSingleton()
    {
        ServiceCollection services = CreateServiceCollection();

        ServiceDescriptor? descriptor = services.FirstOrDefault(
            d => d.ServiceType == typeof(IDomainEventDispatcher));

        descriptor.Should().NotBeNull();
        descriptor!.Lifetime.Should().Be(ServiceLifetime.Singleton);
        descriptor.ImplementationType.Should().Be<DomainEventDispatcher>();
    }

    [Fact]
    public void AddApplication_RegistersNavigationMetadataProvider_AsSingleton()
    {
        ServiceCollection services = CreateServiceCollection();

        ServiceDescriptor? descriptor = services.FirstOrDefault(
            d => d.ServiceType == typeof(INavigationMetadataProvider));

        descriptor.Should().NotBeNull();
        descriptor!.Lifetime.Should().Be(ServiceLifetime.Singleton);
        descriptor.ImplementationType.Should().Be<NavigationMetadataProvider>();
    }

    [Fact]
    public void AddApplication_RegistersEntityQueryPipeline_AsSingleton()
    {
        ServiceCollection services = CreateServiceCollection();

        ServiceDescriptor? descriptor = services.FirstOrDefault(
            d => d.ServiceType == typeof(IEntityQueryPipeline));

        descriptor.Should().NotBeNull();
        descriptor!.Lifetime.Should().Be(ServiceLifetime.Singleton);
        descriptor.ImplementationType.Should().Be<EntityQueryPipeline>();
    }

    [Fact]
    public void AddApplication_RegistersApplicationSettings_AsSingletonFactory()
    {
        ServiceCollection services = CreateServiceCollection();

        ServiceDescriptor? descriptor = services.FirstOrDefault(
            d => d.ServiceType == typeof(IApplicationSettings));

        descriptor.Should().NotBeNull();
        descriptor!.Lifetime.Should().Be(ServiceLifetime.Singleton);

        // IApplicationSettings is registered via a factory delegate that reads IOptions<ApplicationSettings>,
        // so ImplementationType is null — the registration uses ImplementationFactory instead.
        descriptor.ImplementationFactory.Should().NotBeNull();
    }

    [Fact]
    public void AddApplication_ApplicationSettingsFactory_ResolvesFromOptions()
    {
        var services = new ServiceCollection();

        // Register IOptions<ApplicationSettings> so the factory delegate can resolve it.
        services.AddOptions<ApplicationSettings>();
        services.AddApplication();

        using ServiceProvider provider = services.BuildServiceProvider();
        IApplicationSettings settings = provider.GetRequiredService<IApplicationSettings>();

        settings.Should().NotBeNull();
        settings.MaxPageSize.Should().Be(500, "default MaxPageSize is 500");
    }

    // ── AddApplication: idempotency (TryAdd) ──
    [Fact]
    public void AddApplication_CalledTwice_DoesNotDuplicateRegistrations()
    {
        var services = new ServiceCollection();
        services.AddApplication();
        services.AddApplication();

        int dispatcherCount = services.Count(d => d.ServiceType == typeof(IDomainEventDispatcher));
        int navigationCount = services.Count(d => d.ServiceType == typeof(INavigationMetadataProvider));
        int pipelineCount = services.Count(d => d.ServiceType == typeof(IEntityQueryPipeline));
        int settingsCount = services.Count(d => d.ServiceType == typeof(IApplicationSettings));

        dispatcherCount.Should().Be(1);
        navigationCount.Should().Be(1);
        pipelineCount.Should().Be(1);
        settingsCount.Should().Be(1);
    }

    // ── AddApplication: returns service collection for chaining ──
    [Fact]
    public void AddApplication_ReturnsServiceCollection_ForChaining()
    {
        var services = new ServiceCollection();

        IServiceCollection result = services.AddApplication();

        result.Should().BeSameAs(services);
    }

    // ── AddApplicationDecorators: does not throw on empty container ──
    [Fact]
    public void AddApplicationDecorators_WithNoHandlers_DoesNotThrow()
    {
        var services = new ServiceCollection();

        Action act = () => services.AddApplicationDecorators();

        act.Should().NotThrow();
    }

    [Fact]
    public void AddApplicationDecorators_ReturnsServiceCollection_ForChaining()
    {
        var services = new ServiceCollection();

        IServiceCollection result = services.AddApplicationDecorators();

        result.Should().BeSameAs(services);
    }

    // ── AddApplicationProfiling: does not throw on empty container ──
    [Fact]
    public void AddApplicationProfiling_WithNoHandlers_DoesNotThrow()
    {
        var services = new ServiceCollection();

        Action act = () => services.AddApplicationProfiling();

        act.Should().NotThrow();
    }

    [Fact]
    public void AddApplicationProfiling_ReturnsServiceCollection_ForChaining()
    {
        var services = new ServiceCollection();

        IServiceCollection result = services.AddApplicationProfiling();

        result.Should().BeSameAs(services);
    }

    // ── AddApplication: validators from MMCA.Common.Application assembly ──
    [Fact]
    public void AddApplication_RegistersValidatorsFromCommonApplicationAssembly()
    {
        ServiceCollection services = CreateServiceCollection();

        // FluentValidation's AddValidatorsFromAssemblyContaining<ClassReference>() should
        // register at least one validator (e.g. LoginRequestValidator, RefreshTokenRequestValidator).
        bool hasValidators = services.Any(d =>
            d.ServiceType.IsGenericType
            && d.ServiceType.GetGenericTypeDefinition() == typeof(FluentValidation.IValidator<>));

        hasValidators.Should().BeTrue("AddApplication should register validators from the Common.Application assembly");
    }
}

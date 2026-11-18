using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Application.Interfaces.Events;
using MMCA.Common.Application.Services;
using MMCA.Common.Application.Services.Query;

namespace MMCA.Common.Application.Tests;

public sealed class DependencyInjectionTests
{
    private static ServiceCollection CreateServiceCollection()
    {
        var services = new ServiceCollection();
        services.AddApplication();
        return services;
    }

    private static IServiceCollection Invoke(string extension, ServiceCollection services) => extension switch
    {
        "AddApplication" => services.AddApplication(),
        "AddApplicationDecorators" => services.AddApplicationDecorators(),
        "AddApplicationProfiling" => services.AddApplicationProfiling(),
        _ => throw new ArgumentOutOfRangeException(nameof(extension), extension, "Unknown extension."),
    };

    // ── AddApplication: core service registrations ──
    [Theory]
    [InlineData(typeof(IDomainEventDispatcher), typeof(DomainEventDispatcher))]
    [InlineData(typeof(INavigationMetadataProvider), typeof(NavigationMetadataProvider))]
    [InlineData(typeof(IEntityQueryPipeline), typeof(EntityQueryPipeline))]
    public void AddApplication_RegistersTheService_AsSingleton(Type serviceType, Type expectedImplementation)
    {
        ServiceCollection services = CreateServiceCollection();

        ServiceDescriptor? descriptor = services.FirstOrDefault(d => d.ServiceType == serviceType);

        descriptor.Should().NotBeNull();
        descriptor!.Lifetime.Should().Be(ServiceLifetime.Singleton);
        descriptor.ImplementationType.Should().Be(expectedImplementation);
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

        dispatcherCount.Should().Be(1);
        navigationCount.Should().Be(1);
        pipelineCount.Should().Be(1);
    }

    // ── Every registration call returns the service collection for chaining ──
    [Theory]
    [InlineData("AddApplication")]
    [InlineData("AddApplicationDecorators")]
    [InlineData("AddApplicationProfiling")]
    public void Extension_ReturnsServiceCollection_ForChaining(string extension)
    {
        var services = new ServiceCollection();

        IServiceCollection result = Invoke(extension, services);

        result.Should().BeSameAs(services);
    }

    // ── The decorator and profiling passes do not throw on an empty container ──
    [Theory]
    [InlineData("AddApplicationDecorators")]
    [InlineData("AddApplicationProfiling")]
    public void Extension_WithNoHandlers_DoesNotThrow(string extension)
    {
        var services = new ServiceCollection();

        Action act = () => Invoke(extension, services);

        act.Should().NotThrow();
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

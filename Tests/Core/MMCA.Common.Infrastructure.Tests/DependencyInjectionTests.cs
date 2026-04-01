using System.Reflection;
using AwesomeAssertions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Infrastructure.Caching;
using MMCA.Common.Infrastructure.Persistence;
using MMCA.Common.Infrastructure.Services;
using Moq;

namespace MMCA.Common.Infrastructure.Tests;

public sealed class DependencyInjectionTests
{
    // ── AddCaching ──
    [Fact]
    public void AddCaching_WithoutDistributedCache_UsesMemoryCacheService()
    {
        var services = new ServiceCollection();
        services.AddCaching();

        using ServiceProvider provider = services.BuildServiceProvider();
        ICacheService cacheService = provider.GetRequiredService<ICacheService>();

        cacheService.Should().BeOfType<MemoryCacheService>();
    }

    [Fact]
    public void AddCaching_WithMemoryDistributedCache_UsesMemoryCacheService()
    {
        var services = new ServiceCollection();

        // Register MemoryDistributedCache -- this simulates AddDistributedMemoryCache().
        services.AddDistributedMemoryCache();

        services.AddCaching();

        using ServiceProvider provider = services.BuildServiceProvider();
        ICacheService cacheService = provider.GetRequiredService<ICacheService>();

        cacheService.Should().BeOfType<MemoryCacheService>();
    }

    [Fact]
    public void AddCaching_WithRealDistributedCache_UsesDistributedCacheService()
    {
        var services = new ServiceCollection();

        // Register a non-MemoryDistributedCache implementation.
        var mockDistributedCache = new Mock<IDistributedCache>();
        services.AddSingleton(mockDistributedCache.Object);

        services.AddCaching();

        using ServiceProvider provider = services.BuildServiceProvider();
        ICacheService cacheService = provider.GetRequiredService<ICacheService>();

        cacheService.Should().BeOfType<DistributedCacheService>();
    }

    [Fact]
    public void AddCaching_RegistersMemoryCache()
    {
        var services = new ServiceCollection();
        services.AddCaching();

        ServiceDescriptor? descriptor = services.FirstOrDefault(
            d => d.ServiceType == typeof(IMemoryCache));

        descriptor.Should().NotBeNull();
    }

    [Fact]
    public void AddCaching_CalledTwice_DoesNotDuplicateCacheService()
    {
        var services = new ServiceCollection();
        services.AddCaching();
        services.AddCaching();

        int count = services.Count(d => d.ServiceType == typeof(ICacheService));

        count.Should().Be(1);
    }

    [Fact]
    public void AddCaching_ReturnsServiceCollection_ForChaining()
    {
        var services = new ServiceCollection();

        IServiceCollection result = services.AddCaching();

        result.Should().BeSameAs(services);
    }

    // ── AddServices: descriptor presence checks ──
    [Fact]
    public void AddServices_RegistersCorrelationContext()
    {
        var services = new ServiceCollection();
        services.AddServices();

        ServiceDescriptor? descriptor = services.FirstOrDefault(
            d => d.ServiceType == typeof(ICorrelationContext));

        descriptor.Should().NotBeNull();
        descriptor!.Lifetime.Should().Be(ServiceLifetime.Scoped);
        descriptor.ImplementationType.Should().Be<CorrelationContext>();
    }

    [Fact]
    public void AddServices_RegistersCurrentUserService()
    {
        var services = new ServiceCollection();
        services.AddServices();

        ServiceDescriptor? descriptor = services.FirstOrDefault(
            d => d.ServiceType == typeof(ICurrentUserService));

        descriptor.Should().NotBeNull();
        descriptor!.Lifetime.Should().Be(ServiceLifetime.Scoped);
        descriptor.ImplementationType.Should().Be<CurrentUserService>();
    }

    [Fact]
    public void AddServices_RegistersTokenService()
    {
        var services = new ServiceCollection();
        services.AddServices();

        ServiceDescriptor? descriptor = services.FirstOrDefault(
            d => d.ServiceType == typeof(ITokenService));

        descriptor.Should().NotBeNull();
        descriptor!.Lifetime.Should().Be(ServiceLifetime.Scoped);
        descriptor.ImplementationType.Should().Be<TokenService>();
    }

    [Fact]
    public void AddServices_RegistersPasswordHasher()
    {
        var services = new ServiceCollection();
        services.AddServices();

        ServiceDescriptor? descriptor = services.FirstOrDefault(
            d => d.ServiceType == typeof(IPasswordHasher));

        descriptor.Should().NotBeNull();
        descriptor!.Lifetime.Should().Be(ServiceLifetime.Singleton);
        descriptor.ImplementationType.Should().Be<PasswordHasher>();
    }

    [Fact]
    public void AddServices_RegistersEventBus()
    {
        var services = new ServiceCollection();
        services.AddServices();

        ServiceDescriptor? descriptor = services.FirstOrDefault(
            d => d.ServiceType == typeof(IEventBus));

        descriptor.Should().NotBeNull();
        descriptor!.Lifetime.Should().Be(ServiceLifetime.Scoped);
        descriptor.ImplementationType.Should().Be<InProcessEventBus>();
    }

    [Fact]
    public void AddServices_RegistersIntegrationEventPublisher()
    {
        var services = new ServiceCollection();
        services.AddServices();

        ServiceDescriptor? descriptor = services.FirstOrDefault(
            d => d.ServiceType == typeof(IIntegrationEventPublisher));

        descriptor.Should().NotBeNull();
        descriptor!.Lifetime.Should().Be(ServiceLifetime.Scoped);
        descriptor.ImplementationType.Should().Be<IntegrationEventPublisher>();
    }

    [Fact]
    public void AddServices_RegistersEmailSender()
    {
        var services = new ServiceCollection();
        services.AddServices();

        ServiceDescriptor? descriptor = services.FirstOrDefault(
            d => d.ServiceType == typeof(IEmailSender));

        descriptor.Should().NotBeNull();
        descriptor!.Lifetime.Should().Be(ServiceLifetime.Transient);
        descriptor.ImplementationType.Should().Be<SmtpEmailSender>();
    }

    [Fact]
    public void AddServices_RegistersPushNotificationSender()
    {
        var services = new ServiceCollection();
        services.AddServices();

        ServiceDescriptor? descriptor = services.FirstOrDefault(
            d => d.ServiceType == typeof(IPushNotificationSender));

        descriptor.Should().NotBeNull();
        descriptor!.Lifetime.Should().Be(ServiceLifetime.Transient);
        descriptor.ImplementationType.Should().Be<NullPushNotificationSender>();
    }

    [Fact]
    public void AddServices_RegistersTimeProvider()
    {
        var services = new ServiceCollection();
        services.AddServices();

        ServiceDescriptor? descriptor = services.FirstOrDefault(
            d => d.ServiceType == typeof(TimeProvider));

        descriptor.Should().NotBeNull();
        descriptor!.Lifetime.Should().Be(ServiceLifetime.Singleton);
    }

    [Fact]
    public void AddServices_RegistersHttpContextAccessor()
    {
        var services = new ServiceCollection();
        services.AddServices();

        ServiceDescriptor? descriptor = services.FirstOrDefault(
            d => d.ServiceType == typeof(Microsoft.AspNetCore.Http.IHttpContextAccessor));

        descriptor.Should().NotBeNull();
    }

    [Fact]
    public void AddServices_CalledTwice_DoesNotDuplicateRegistrations()
    {
        var services = new ServiceCollection();
        services.AddServices();
        services.AddServices();

        int passwordHasherCount = services.Count(d => d.ServiceType == typeof(IPasswordHasher));
        int eventBusCount = services.Count(d => d.ServiceType == typeof(IEventBus));
        int emailSenderCount = services.Count(d => d.ServiceType == typeof(IEmailSender));
        int pushSenderCount = services.Count(d => d.ServiceType == typeof(IPushNotificationSender));

        passwordHasherCount.Should().Be(1);
        eventBusCount.Should().Be(1);
        emailSenderCount.Should().Be(1);
        pushSenderCount.Should().Be(1);
    }

    [Fact]
    public void AddServices_ReturnsServiceCollection_ForChaining()
    {
        var services = new ServiceCollection();

        IServiceCollection result = services.AddServices();

        result.Should().BeSameAs(services);
    }

    // ── AddEntityConfigurationAssembly ──
    [Fact]
    public void AddEntityConfigurationAssembly_AddsAssemblyToOptions()
    {
        var services = new ServiceCollection();
        Assembly testAssembly = typeof(DependencyInjectionTests).Assembly;

        services.AddEntityConfigurationAssembly(testAssembly);

        using ServiceProvider provider = services.BuildServiceProvider();
        EntityConfigurationOptions options = provider.GetRequiredService<IOptions<EntityConfigurationOptions>>().Value;

        options.AdditionalAssemblies.Should().ContainSingle()
            .Which.Should().BeSameAs(testAssembly);
    }

    [Fact]
    public void AddEntityConfigurationAssembly_CalledTwiceWithSameAssembly_DoesNotDuplicate()
    {
        var services = new ServiceCollection();
        Assembly testAssembly = typeof(DependencyInjectionTests).Assembly;

        services.AddEntityConfigurationAssembly(testAssembly);
        services.AddEntityConfigurationAssembly(testAssembly);

        using ServiceProvider provider = services.BuildServiceProvider();
        EntityConfigurationOptions options = provider.GetRequiredService<IOptions<EntityConfigurationOptions>>().Value;

        options.AdditionalAssemblies.Should().ContainSingle();
    }

    [Fact]
    public void AddEntityConfigurationAssembly_CalledWithDifferentAssemblies_AddsBoth()
    {
        var services = new ServiceCollection();
        Assembly assembly1 = typeof(DependencyInjectionTests).Assembly;
        Assembly assembly2 = typeof(ICacheService).Assembly;

        services.AddEntityConfigurationAssembly(assembly1);
        services.AddEntityConfigurationAssembly(assembly2);

        using ServiceProvider provider = services.BuildServiceProvider();
        EntityConfigurationOptions options = provider.GetRequiredService<IOptions<EntityConfigurationOptions>>().Value;

        options.AdditionalAssemblies.Should().HaveCount(2);
        options.AdditionalAssemblies.Should().Contain(assembly1);
        options.AdditionalAssemblies.Should().Contain(assembly2);
    }

    [Fact]
    public void AddEntityConfigurationAssembly_ReturnsServiceCollection_ForChaining()
    {
        var services = new ServiceCollection();

        IServiceCollection result = services.AddEntityConfigurationAssembly(typeof(DependencyInjectionTests).Assembly);

        result.Should().BeSameAs(services);
    }
}

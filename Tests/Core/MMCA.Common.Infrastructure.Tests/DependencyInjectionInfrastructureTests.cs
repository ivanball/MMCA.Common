using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Infrastructure.Persistence.Interceptors;
using MMCA.Common.Infrastructure.Settings;

namespace MMCA.Common.Infrastructure.Tests;

/// <summary>
/// Tests for <see cref="DependencyInjection.AddInfrastructure"/> covering service registrations
/// that are not yet covered by DependencyInjectionAdditionalTests.
/// </summary>
public sealed class DependencyInjectionInfrastructureTests
{
    private static IConfiguration CreateMinimalConfiguration()
    {
        var configData = new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = "Server=test;Database=test",
            ["ConnectionStrings:CosmosConnection"] = "AccountEndpoint=https://test;AccountKey=dGVzdA==",
            ["Smtp:Host"] = "smtp.test.com",
            ["Smtp:Port"] = "587",
            ["Smtp:Username"] = "user",
            ["Smtp:Password"] = "pass",
            ["Smtp:From"] = "from@test.com",
            ["Smtp:To"] = "to@test.com",
            ["Smtp:EnableSsl"] = "true",
            ["Jwt:SecretForKey"] = "dGVzdGtleXRoYXRpc2xvbmdlbm91Z2hmb3JiYXNlNjQ=",
            ["Jwt:Issuer"] = "https://test",
            ["Jwt:Audience"] = "test",
            ["Jwt:AccessTokenExpirationMinutes"] = "30",
            ["Jwt:RefreshTokenExpirationDays"] = "7",
            ["Outbox:DataSource"] = "SQLServer",
        };

        return new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();
    }

    [Fact]
    public void AddInfrastructure_RegistersEntityConfigurationAssemblyProvider()
    {
        var services = new ServiceCollection();
        IConfiguration config = CreateMinimalConfiguration();

        services.AddInfrastructure(config);

        ServiceDescriptor? descriptor = services.FirstOrDefault(
            d => d.ServiceType == typeof(IEntityConfigurationAssemblyProvider));

        descriptor.Should().NotBeNull();
        descriptor!.Lifetime.Should().Be(ServiceLifetime.Singleton);
    }

    [Fact]
    public void AddInfrastructure_RegistersAuditSaveChangesInterceptor()
    {
        var services = new ServiceCollection();
        IConfiguration config = CreateMinimalConfiguration();

        services.AddInfrastructure(config);

        ServiceDescriptor? descriptor = services.FirstOrDefault(
            d => d.ServiceType == typeof(AuditSaveChangesInterceptor));

        descriptor.Should().NotBeNull();
        descriptor!.Lifetime.Should().Be(ServiceLifetime.Singleton);
    }

    [Fact]
    public void AddInfrastructure_RegistersDomainEventSaveChangesInterceptor()
    {
        var services = new ServiceCollection();
        IConfiguration config = CreateMinimalConfiguration();

        services.AddInfrastructure(config);

        ServiceDescriptor? descriptor = services.FirstOrDefault(
            d => d.ServiceType == typeof(DomainEventSaveChangesInterceptor));

        descriptor.Should().NotBeNull();
        descriptor!.Lifetime.Should().Be(ServiceLifetime.Singleton);
    }

    [Fact]
    public void AddInfrastructure_RegistersSmtpSettingsOptions()
    {
        var services = new ServiceCollection();
        IConfiguration config = CreateMinimalConfiguration();

        services.AddInfrastructure(config);

        ServiceDescriptor? descriptor = services.FirstOrDefault(
            d => d.ServiceType == typeof(IConfigureOptions<SmtpSettings>));

        descriptor.Should().NotBeNull();
    }

    [Fact]
    public void AddInfrastructure_RegistersConnectionStringSettingsOptions()
    {
        var services = new ServiceCollection();
        IConfiguration config = CreateMinimalConfiguration();

        services.AddInfrastructure(config);

        ServiceDescriptor? descriptor = services.FirstOrDefault(
            d => d.ServiceType == typeof(IConfigureOptions<ConnectionStringSettings>));

        descriptor.Should().NotBeNull();
    }

    [Fact]
    public void AddInfrastructure_RegistersOutboxSettingsOptions()
    {
        var services = new ServiceCollection();
        IConfiguration config = CreateMinimalConfiguration();

        services.AddInfrastructure(config);

        ServiceDescriptor? descriptor = services.FirstOrDefault(
            d => d.ServiceType == typeof(IConfigureOptions<OutboxSettings>));

        descriptor.Should().NotBeNull();
    }

    [Fact]
    public void AddInfrastructure_RegistersOutboxProcessorHostedService()
    {
        var services = new ServiceCollection();
        IConfiguration config = CreateMinimalConfiguration();

        services.AddInfrastructure(config);

        ServiceDescriptor? descriptor = services.FirstOrDefault(
            d => d.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService) &&
                d.ImplementationType == typeof(Infrastructure.Persistence.Outbox.OutboxProcessor));

        descriptor.Should().NotBeNull();
    }

    [Fact]
    public void AddInfrastructure_RegistersJwtSettings()
    {
        var services = new ServiceCollection();
        IConfiguration config = CreateMinimalConfiguration();

        services.AddInfrastructure(config);

        ServiceDescriptor? descriptor = services.FirstOrDefault(
            d => d.ServiceType == typeof(IJwtSettings));

        descriptor.Should().NotBeNull();
        descriptor!.Lifetime.Should().Be(ServiceLifetime.Singleton);
    }

    [Fact]
    public void AddInfrastructure_RegistersGenericRepositoryInterface()
    {
        var services = new ServiceCollection();
        IConfiguration config = CreateMinimalConfiguration();

        services.AddInfrastructure(config);

        ServiceDescriptor? descriptor = services.FirstOrDefault(
            d => d.ServiceType == typeof(IRepository<,>));

        descriptor.Should().NotBeNull();
        descriptor!.Lifetime.Should().Be(ServiceLifetime.Scoped);
    }

    [Fact]
    public void AddInfrastructure_CalledTwice_DoesNotDuplicateRegistrations()
    {
        var services = new ServiceCollection();
        IConfiguration config = CreateMinimalConfiguration();

        services.AddInfrastructure(config);
        services.AddInfrastructure(config);

        int dataSourceServiceCount = services.Count(d => d.ServiceType == typeof(IDataSourceService));
        int unitOfWorkCount = services.Count(d => d.ServiceType == typeof(IUnitOfWork));

        dataSourceServiceCount.Should().Be(1);
        unitOfWorkCount.Should().Be(1);
    }

    [Fact]
    public void AddNotificationInfrastructure_ReturnsServiceCollection_ForChaining()
    {
        var services = new ServiceCollection();

        IServiceCollection result = services.AddNotificationInfrastructure();

        result.Should().BeSameAs(services);
    }

    [Fact]
    public void AddEntityConfigurationAssembly_RegistersAssemblyInOptions()
    {
        var services = new ServiceCollection();
        var assembly = typeof(DependencyInjectionInfrastructureTests).Assembly;

        services.AddEntityConfigurationAssembly(assembly);

        ServiceDescriptor? descriptor = services.FirstOrDefault(
            d => d.ServiceType == typeof(Microsoft.Extensions.Options.IConfigureOptions<Infrastructure.Persistence.EntityConfigurationOptions>));

        descriptor.Should().NotBeNull();
    }

    [Fact]
    public void AddEntityConfigurationAssembly_CalledTwiceWithSameAssembly_DoesNotDuplicate()
    {
        var services = new ServiceCollection();
        var assembly = typeof(DependencyInjectionInfrastructureTests).Assembly;

        services.AddEntityConfigurationAssembly(assembly);
        services.AddEntityConfigurationAssembly(assembly);

        using ServiceProvider sp = services.BuildServiceProvider();
        var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<Infrastructure.Persistence.EntityConfigurationOptions>>().Value;

        options.AdditionalAssemblies.Count(a => a == assembly).Should().Be(1);
    }

    [Fact]
    public void AddInfrastructure_RegistersISmtpSettings()
    {
        var services = new ServiceCollection();
        IConfiguration config = CreateMinimalConfiguration();

        services.AddInfrastructure(config);

        ServiceDescriptor? descriptor = services.FirstOrDefault(
            d => d.ServiceType == typeof(ISmtpSettings));

        descriptor.Should().NotBeNull();
        descriptor!.Lifetime.Should().Be(ServiceLifetime.Singleton);
    }

    [Fact]
    public void AddInfrastructure_RegistersIConnectionStringSettings()
    {
        var services = new ServiceCollection();
        IConfiguration config = CreateMinimalConfiguration();

        services.AddInfrastructure(config);

        ServiceDescriptor? descriptor = services.FirstOrDefault(
            d => d.ServiceType == typeof(IConnectionStringSettings));

        descriptor.Should().NotBeNull();
        descriptor!.Lifetime.Should().Be(ServiceLifetime.Singleton);
    }

    [Fact]
    public void AddInfrastructure_RegistersIQueryableExecutor()
    {
        var services = new ServiceCollection();
        IConfiguration config = CreateMinimalConfiguration();

        services.AddInfrastructure(config);

        ServiceDescriptor? descriptor = services.FirstOrDefault(
            d => d.ServiceType == typeof(IQueryableExecutor));

        descriptor.Should().NotBeNull();
        descriptor!.Lifetime.Should().Be(ServiceLifetime.Singleton);
    }

    [Fact]
    public void AddInfrastructure_RegistersIRepositoryFactory()
    {
        var services = new ServiceCollection();
        IConfiguration config = CreateMinimalConfiguration();

        services.AddInfrastructure(config);

        ServiceDescriptor? descriptor = services.FirstOrDefault(
            d => d.ServiceType == typeof(Infrastructure.Persistence.Repositories.Factory.IRepositoryFactory));

        descriptor.Should().NotBeNull();
        descriptor!.Lifetime.Should().Be(ServiceLifetime.Scoped);
    }
}

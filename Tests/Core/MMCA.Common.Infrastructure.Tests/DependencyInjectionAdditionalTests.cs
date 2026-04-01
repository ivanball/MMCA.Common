using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Infrastructure.Persistence;
using MMCA.Common.Infrastructure.Persistence.DbContexts.Factory;
using MMCA.Common.Infrastructure.Persistence.Repositories.Factory;
using MMCA.Common.Infrastructure.Settings;

namespace MMCA.Common.Infrastructure.Tests;

public sealed class DependencyInjectionAdditionalTests
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

    // ── AddNotificationInfrastructure ──
    [Fact]
    public void AddNotificationInfrastructure_AddsNotificationConfigAssembly()
    {
        var services = new ServiceCollection();

        services.AddNotificationInfrastructure();

        using ServiceProvider provider = services.BuildServiceProvider();
        EntityConfigurationOptions options = provider.GetRequiredService<IOptions<EntityConfigurationOptions>>().Value;

        options.AdditionalAssemblies.Should().NotBeEmpty();
    }

    [Fact]
    public void AddNotificationInfrastructure_ReturnsServiceCollection_ForChaining()
    {
        var services = new ServiceCollection();

        IServiceCollection result = services.AddNotificationInfrastructure();

        result.Should().BeSameAs(services);
    }

    // ── AddInfrastructure registers core services ──
    [Fact]
    public void AddInfrastructure_RegistersDataSourceService()
    {
        var services = new ServiceCollection();
        IConfiguration config = CreateMinimalConfiguration();

        services.AddInfrastructure(config);

        ServiceDescriptor? descriptor = services.FirstOrDefault(
            d => d.ServiceType == typeof(IDataSourceService));

        descriptor.Should().NotBeNull();
        descriptor!.Lifetime.Should().Be(ServiceLifetime.Singleton);
    }

    [Fact]
    public void AddInfrastructure_RegistersDbContextFactory()
    {
        var services = new ServiceCollection();
        IConfiguration config = CreateMinimalConfiguration();

        services.AddInfrastructure(config);

        ServiceDescriptor? descriptor = services.FirstOrDefault(
            d => d.ServiceType == typeof(IDbContextFactory));

        descriptor.Should().NotBeNull();
        descriptor!.Lifetime.Should().Be(ServiceLifetime.Scoped);
    }

    [Fact]
    public void AddInfrastructure_RegistersUnitOfWork()
    {
        var services = new ServiceCollection();
        IConfiguration config = CreateMinimalConfiguration();

        services.AddInfrastructure(config);

        ServiceDescriptor? descriptor = services.FirstOrDefault(
            d => d.ServiceType == typeof(IUnitOfWork));

        descriptor.Should().NotBeNull();
        descriptor!.Lifetime.Should().Be(ServiceLifetime.Scoped);
    }

    [Fact]
    public void AddInfrastructure_RegistersRepositoryFactory()
    {
        var services = new ServiceCollection();
        IConfiguration config = CreateMinimalConfiguration();

        services.AddInfrastructure(config);

        ServiceDescriptor? descriptor = services.FirstOrDefault(
            d => d.ServiceType == typeof(IRepositoryFactory));

        descriptor.Should().NotBeNull();
        descriptor!.Lifetime.Should().Be(ServiceLifetime.Scoped);
    }

    [Fact]
    public void AddInfrastructure_RegistersQueryableExecutor()
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
    public void AddInfrastructure_ReturnsServiceCollection_ForChaining()
    {
        var services = new ServiceCollection();
        IConfiguration config = CreateMinimalConfiguration();

        IServiceCollection result = services.AddInfrastructure(config);

        result.Should().BeSameAs(services);
    }
}

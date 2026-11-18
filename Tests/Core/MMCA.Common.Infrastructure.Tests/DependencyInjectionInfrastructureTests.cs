using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Application.Users.UseCases.ExportUserData;
using MMCA.Common.Infrastructure.Mail;
using MMCA.Common.Infrastructure.Persistence;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DbContexts.Factory;
using MMCA.Common.Infrastructure.Persistence.Interceptors;
using MMCA.Common.Infrastructure.Persistence.Outbox.Administration;
using MMCA.Common.Infrastructure.Persistence.Outbox.Processing;

namespace MMCA.Common.Infrastructure.Tests;

/// <summary>
/// Tests for <see cref="DependencyInjection.AddInfrastructure"/> service registrations,
/// plus AddNotificationInfrastructure.
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
    public void AddInfrastructure_RegistersTheSignInSessionsExportSection_Once()
    {
        var services = new ServiceCollection();
        IConfiguration config = CreateMinimalConfiguration();

        services.AddInfrastructure(config);
        services.AddInfrastructure(config);

        services.Where(d => d.ServiceType == typeof(IUserDataExportSection))
            .Should().ContainSingle(
                "every host that issues refresh sessions exports them with no app code, and a repeated call adds nothing")
            .Which.ImplementationType.Should().Be<RefreshSessionUserDataExportSection>();
    }

    // A null lifetime means that row asserts presence only (the IConfigureOptions<T> bindings).
    [Theory]
    [InlineData(typeof(IEntityConfigurationAssemblyProvider), ServiceLifetime.Singleton)]
    [InlineData(typeof(AuditSaveChangesInterceptor), ServiceLifetime.Singleton)]
    [InlineData(typeof(DomainEventSaveChangesInterceptor), ServiceLifetime.Singleton)]
    [InlineData(typeof(IConfigureOptions<SmtpSettings>), null)]
    [InlineData(typeof(IConfigureOptions<ConnectionStringSettings>), null)]
    [InlineData(typeof(IConfigureOptions<OutboxSettings>), null)]
    [InlineData(typeof(IRepository<,>), ServiceLifetime.Scoped)]
    [InlineData(typeof(IQueryableExecutor), ServiceLifetime.Singleton)]
    [InlineData(typeof(Infrastructure.Persistence.Repositories.Factory.IRepositoryFactory), ServiceLifetime.Scoped)]
    [InlineData(typeof(IDataSourceService), ServiceLifetime.Singleton)]
    [InlineData(typeof(IDbContextFactory), ServiceLifetime.Scoped)]
    [InlineData(typeof(IUnitOfWork), ServiceLifetime.Scoped)]
    public void AddInfrastructure_RegistersTheService(Type serviceType, ServiceLifetime? expectedLifetime)
    {
        var services = new ServiceCollection();
        IConfiguration config = CreateMinimalConfiguration();

        services.AddInfrastructure(config);

        ServiceDescriptor? descriptor = services.FirstOrDefault(d => d.ServiceType == serviceType);

        descriptor.Should().NotBeNull();
        if (expectedLifetime is not null)
        {
            descriptor!.Lifetime.Should().Be(expectedLifetime.Value);
        }
    }

    // The processor is registered for a host that runs the outbox. Whether a given host does is the
    // transport's decision (MessageBus:EnableOutbox / the provider), covered by
    // DependencyInjectionOutboxGateTests; this case pins that turning it on still wires the drain loop.
    [Fact]
    public void AddInfrastructure_WithTheOutboxEnabled_RegistersOutboxProcessorHostedService()
    {
        var services = new ServiceCollection();
        IConfiguration config = new ConfigurationBuilder()
            .AddConfiguration(CreateMinimalConfiguration())
            .AddInMemoryCollection(new Dictionary<string, string?> { ["MessageBus:EnableOutbox"] = "true" })
            .Build();

        services.AddInfrastructure(config);

        ServiceDescriptor? descriptor = services.FirstOrDefault(
            d => d.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService) &&
                d.ImplementationType == typeof(OutboxProcessor));

        descriptor.Should().NotBeNull();
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
    public void AddInfrastructure_RegistersTheUniqueConstraintViolationDetector()
    {
        var services = new ServiceCollection();
        IConfiguration config = CreateMinimalConfiguration();

        services.AddInfrastructure(config);

        ServiceDescriptor? descriptor = services.FirstOrDefault(
            d => d.ServiceType == typeof(IUniqueConstraintViolationDetector));

        descriptor.Should().NotBeNull();
        descriptor!.ImplementationType.Should().Be<Infrastructure.Persistence.SqlServerUniqueConstraintViolationDetector>();
        descriptor.Lifetime.Should().Be(
            ServiceLifetime.Singleton,
            "the detector holds nothing between calls and reads only the exception handed to it");
    }

    [Fact]
    public void AddInfrastructure_RelationalDefaultEngine_RegistersTheRawSqlQueryExecutor()
    {
        var services = new ServiceCollection();
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:SqliteConnectionString"] = "DataSource=:memory:",
            })
            .Build();

        services.AddInfrastructure(config);

        ServiceDescriptor? descriptor = services.FirstOrDefault(
            d => d.ServiceType == typeof(IRawSqlQueryExecutor));

        descriptor.Should().NotBeNull(
            "the host's default source resolves to SQLite, a relational engine that can run raw SQL");
        descriptor!.Lifetime.Should().Be(ServiceLifetime.Scoped);
    }

    [Fact]
    public void AddInfrastructure_CosmosDefaultEngine_DoesNotRegisterTheRawSqlQueryExecutor()
    {
        var services = new ServiceCollection();
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:CosmosConnectionString"] = "AccountEndpoint=https://localhost:8081/;AccountKey=dGVzdA==",
            })
            .Build();

        services.AddInfrastructure(config);

        services.Should().NotContain(
            d => d.ServiceType == typeof(IRawSqlQueryExecutor),
            "a Cosmos-default host has no SQL command surface, so injecting the executor must fail at container validation instead of on the first statement");
    }

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
    public void AddInfrastructure_ReturnsServiceCollection_ForChaining()
    {
        var services = new ServiceCollection();
        IConfiguration config = CreateMinimalConfiguration();

        IServiceCollection result = services.AddInfrastructure(config);

        result.Should().BeSameAs(services);
    }
}

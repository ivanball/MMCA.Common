using System.Reflection;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Infrastructure.Persistence;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Persistence.Interceptors;
using MMCA.Common.Infrastructure.Persistence.Outbox;
using MMCA.Common.Infrastructure.Tests.TestDoubles;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Persistence.DbContexts;

/// <summary>
/// The SQL command timeout is configured in <c>OnConfiguring</c> from
/// <see cref="PersistenceSettings"/>. It is resolved with <c>GetService</c> rather than
/// <c>GetRequiredService</c> so the design-time provider behind <c>dotnet ef</c> (which registers
/// no options) keeps working, and it is never cached statically because these contexts are
/// deliberately never pooled.
/// </summary>
public sealed class SQLServerDbContextTests
{
    [Fact]
    public void CommandTimeout_OptionsRegistered_UsesTheConfiguredValue()
    {
        using var serviceProvider = BuildServiceProvider(new PersistenceSettings { CommandTimeoutSeconds = 45 });
        using var context = CreateContext(serviceProvider);

        context.Database.GetCommandTimeout().Should().Be(45);
    }

    [Fact]
    public void CommandTimeout_NoOptionsRegistered_FallsBackToThirty()
    {
        using var serviceProvider = BuildServiceProvider(settings: null);
        using var context = CreateContext(serviceProvider);

        context.Database.GetCommandTimeout().Should().Be(
            30,
            "a design-time provider registers no options, and the fallback must reproduce the previous implicit ADO.NET default");
    }

    private static SQLServerDbContext CreateContext(IServiceProvider serviceProvider) =>
        new(
            new DbContextOptionsBuilder<SQLServerDbContext>().Options,
            serviceProvider,
            new EmptyAssemblyProvider(),
            TestPhysicalDataSources.SqlServer());

    private static ServiceProvider BuildServiceProvider(PersistenceSettings? settings)
    {
        var services = new ServiceCollection()
            .AddSingleton(TimeProvider.System)
            .AddSingleton<ILoggerFactory, NullLoggerFactory>()
            .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
            .AddSingleton(Mock.Of<IDomainEventDispatcher>())
            .AddSingleton<IOutboxSignal, OutboxSignal>()
            .AddSingleton<AuditSaveChangesInterceptor>()
            .AddSingleton<DomainEventSaveChangesInterceptor>()
            .AddSingleton<IEntityDataSourceRegistry>(new EmptyEntityDataSourceRegistry());

        if (settings is not null)
        {
            services.AddSingleton<IOptions<PersistenceSettings>>(Options.Create(settings));
        }

        return services.BuildServiceProvider();
    }

    private sealed class EmptyAssemblyProvider : IEntityConfigurationAssemblyProvider
    {
        public IReadOnlyList<Assembly> GetConfigurationAssemblies() => [];
    }
}

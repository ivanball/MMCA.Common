#pragma warning disable CA2000 // Dispose objects before losing scope: BackgroundService lifecycle managed by test
#pragma warning disable CA1873 // Log-argument evaluation warning misfires inside the Moq ILogger.Log verify expression; nothing is logged there.

using System.Reflection;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.Outbox;
using MMCA.Common.Infrastructure.Settings;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Persistence;

/// <summary>
/// Partial coverage for <see cref="OutboxCleanupService"/>: the RetentionDays-disabled path,
/// immediate-shutdown behavior, and the private relational-source selection helper (via
/// reflection, mirroring the existing non-public-member test pattern in this project).
/// The purge sweep itself is deliberately NOT driven here: <c>CleanupIntervalHours</c> is an
/// integer number of hours, so the first sweep cannot occur within a unit-test time budget
/// without a TimeProvider seam (a Source change, out of scope for this test-only wave).
/// </summary>
public sealed class OutboxCleanupServiceTests
{
    // ── Mocks ──
    private sealed record Mocks(
        Mock<IServiceScopeFactory> ScopeFactory,
        Mock<ILogger<OutboxCleanupService>> Logger,
        Mock<IEntityDataSourceRegistry> Registry,
        Mock<IDataSourceResolver> Resolver);

    // ── Factory ──
    private static (OutboxCleanupService Sut, Mocks Mocks) CreateSut(
        OutboxSettings outboxSettings,
        MessageBusSettings? messageBusSettings = null)
    {
        var scopeFactory = new Mock<IServiceScopeFactory>();

        var logger = new Mock<ILogger<OutboxCleanupService>>();
        logger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

        var registry = new Mock<IEntityDataSourceRegistry>();
        registry.Setup(r => r.GetPhysicalSourcesInUse()).Returns([]);

        var resolver = new Mock<IDataSourceResolver>();
        resolver
            .Setup(r => r.ResolveLogical(It.IsAny<DataSource>(), It.IsAny<string>()))
            .Returns((DataSource engine, string _) => DataSourceKey.Default(engine));

        var sut = new OutboxCleanupService(
            scopeFactory.Object,
            logger.Object,
            Options.Create(outboxSettings),
            Options.Create(messageBusSettings ?? new MessageBusSettings()),
            registry.Object,
            resolver.Object);

        return (sut, new Mocks(scopeFactory, logger, registry, resolver));
    }

    // ── Disabled path: RetentionDays 0 logs and exits without touching the store ──
    [Fact]
    public async Task ExecuteAsync_RetentionDaysZero_LogsDisabledAndNeverTouchesTheStore()
    {
        var (sut, mocks) = CreateSut(new OutboxSettings { RetentionDays = 0 });
        using var service = sut;

        await service.StartAsync(CancellationToken.None);
        await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(30));
        await service.StopAsync(CancellationToken.None);

        service.ExecuteTask.IsCompletedSuccessfully.Should().BeTrue(
            "the disabled path should return immediately without faulting");
        mocks.ScopeFactory.Verify(f => f.CreateScope(), Times.Never);
        mocks.Registry.Verify(r => r.GetPhysicalSourcesInUse(), Times.Never);
        // The LoggerMessage source generator names the EventId after the logging method, and its
        // LoggerMessageState does not expose the formatted message via ToString(), so the event
        // id is the stable way to assert THIS specific log entry was written.
        mocks.Logger.Verify(
            l => l.Log(
                LogLevel.Information,
                It.Is<EventId>(e => e.Name == "LogCleanupDisabled"),
                It.IsAny<It.IsAnyType>(),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    // ── Disabled path: a negative retention behaves like 0 (guard is <= 0) ──
    [Fact]
    public async Task ExecuteAsync_NegativeRetentionDays_ExitsWithoutPurging()
    {
        var (sut, mocks) = CreateSut(new OutboxSettings { RetentionDays = -1 });
        using var service = sut;

        await service.StartAsync(CancellationToken.None);
        await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(30));
        await service.StopAsync(CancellationToken.None);

        service.ExecuteTask.IsCompletedSuccessfully.Should().BeTrue();
        mocks.ScopeFactory.Verify(f => f.CreateScope(), Times.Never);
    }

    // ── Shutdown: StopAsync during the initial interval wait completes promptly, no sweep ──
    [Fact]
    public async Task StopAsync_DuringInitialIntervalWait_ShutsDownPromptlyWithoutSweeping()
    {
        var (sut, mocks) = CreateSut(new OutboxSettings { RetentionDays = 7, CleanupIntervalHours = 1 });
        using var service = sut;

        await service.StartAsync(CancellationToken.None);
        service.ExecuteTask!.IsCompleted.Should().BeFalse(
            "the service should be waiting out the first cleanup interval, not running or finished");

        await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));

        // .NET 10's BackgroundService dispatches ExecuteAsync via Task.Run(..., stoppingToken), so a
        // stop that wins the race against the thread pool cancels ExecuteTask outright before
        // ExecuteAsync ever runs (task ends Canceled, not RanToCompletion). Both outcomes are a
        // graceful prompt shutdown; the contract to pin is completed-without-fault and no sweep.
        service.ExecuteTask.IsCompleted.Should().BeTrue("StopAsync must not return before the execute task settles");
        service.ExecuteTask.IsFaulted.Should().BeFalse(
            "cancellation during the interval wait must exit the loop gracefully, not fault");
        mocks.ScopeFactory.Verify(
            f => f.CreateScope(),
            Times.Never,
            "no purge sweep should run when the service is stopped before the first interval elapses");
    }

    // ── Shutdown: cancelling the start token during the interval wait also exits gracefully ──
    [Fact]
    public async Task ExecuteAsync_StartTokenCancelledDuringIntervalWait_ExitsGracefully()
    {
        var (sut, mocks) = CreateSut(new OutboxSettings { RetentionDays = 7, CleanupIntervalHours = 1 });
        using var service = sut;
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await service.StartAsync(cts.Token);
        try
        {
            await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch (OperationCanceledException)
        {
            // .NET 10's BackgroundService dispatches ExecuteAsync via Task.Run(..., stoppingToken):
            // when the 100ms token fires before the pool runs the work item, ExecuteTask ends
            // Canceled without ExecuteAsync ever starting. That is still a graceful non-faulted exit.
        }

        await service.StopAsync(CancellationToken.None);

        service.ExecuteTask!.IsCompleted.Should().BeTrue();
        service.ExecuteTask.IsFaulted.Should().BeFalse(
            "cancelling the start token during the interval wait must exit gracefully, not fault");
        mocks.ScopeFactory.Verify(f => f.CreateScope(), Times.Never);
    }

    // ── Source selection: Cosmos sources are excluded, the publish target is appended ──
    [Fact]
    public void GetRelationalSources_ExcludesCosmosSourcesAndAppendsPublishTarget()
    {
        var (sut, mocks) = CreateSut(new OutboxSettings
        {
            DataSource = DataSource.SQLServer,
            DatabaseName = "Outbox",
        });
        using var service = sut;
        var conference = new DataSourceKey(DataSource.SQLServer, "Conference");
        var engagement = new DataSourceKey(DataSource.CosmosDB, "Engagement");
        var publishTarget = new DataSourceKey(DataSource.SQLServer, "Outbox");
        mocks.Registry.Setup(r => r.GetPhysicalSourcesInUse()).Returns([conference, engagement]);
        mocks.Resolver
            .Setup(r => r.ResolveLogical(DataSource.SQLServer, "Outbox"))
            .Returns(publishTarget);

        List<DataSourceKey> sources = InvokeGetRelationalSources(service);

        sources.Should().Equal(conference, publishTarget);
    }

    // ── Source selection: a Cosmos publish target is never appended (and never resolved) ──
    [Fact]
    public void GetRelationalSources_CosmosPublishTarget_DoesNotAppendTarget()
    {
        var (sut, mocks) = CreateSut(new OutboxSettings { DataSource = DataSource.CosmosDB });
        using var service = sut;
        var conference = new DataSourceKey(DataSource.SQLServer, "Conference");
        mocks.Registry.Setup(r => r.GetPhysicalSourcesInUse()).Returns([conference]);

        List<DataSourceKey> sources = InvokeGetRelationalSources(service);

        sources.Should().Equal(conference);
        mocks.Resolver.Verify(
            r => r.ResolveLogical(It.IsAny<DataSource>(), It.IsAny<string>()),
            Times.Never);
    }

    // ── Source selection: a registry source equal to the publish target is deduplicated ──
    [Fact]
    public void GetRelationalSources_PublishTargetAlreadyInRegistry_IsDeduplicated()
    {
        var (sut, mocks) = CreateSut(new OutboxSettings { DataSource = DataSource.SQLServer });
        using var service = sut;
        var defaultSqlServer = DataSourceKey.Default(DataSource.SQLServer);
        mocks.Registry.Setup(r => r.GetPhysicalSourcesInUse()).Returns([defaultSqlServer]);

        List<DataSourceKey> sources = InvokeGetRelationalSources(service);

        sources.Should().Equal(defaultSqlServer);
    }

    // ── Helpers ──

    /// <summary>
    /// Invokes the private <c>GetRelationalSources</c> helper via reflection so the pure
    /// source-selection logic can be verified without driving the hour-scale purge loop.
    /// </summary>
    private static List<DataSourceKey> InvokeGetRelationalSources(OutboxCleanupService sut)
    {
        MethodInfo? method = typeof(OutboxCleanupService)
            .GetMethod("GetRelationalSources", BindingFlags.NonPublic | BindingFlags.Instance);
        method.Should().NotBeNull();

        return (List<DataSourceKey>)method!.Invoke(sut, [])!;
    }
}

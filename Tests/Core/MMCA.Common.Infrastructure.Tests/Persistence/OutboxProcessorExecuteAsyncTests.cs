using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.Outbox;
using MMCA.Common.Infrastructure.Settings;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Persistence;

/// <summary>
/// Tests for <see cref="OutboxProcessor.ExecuteAsync"/> edge cases that are not covered
/// by the ProcessPendingMessagesAsync tests in OutboxProcessorTests. The processor's
/// <see cref="TimeProvider"/> seam is driven with a <see cref="FakeTimeProvider"/> so the
/// 5-second startup delay elapses instantly — no test sleeps real seconds.
/// </summary>
public sealed class OutboxProcessorExecuteAsyncTests
{
    private static Mock<IEntityDataSourceRegistry> CreateEmptyRegistryMock()
    {
        var registryMock = new Mock<IEntityDataSourceRegistry>();
        registryMock.Setup(r => r.GetPhysicalSourcesInUse()).Returns([]);
        return registryMock;
    }

    private static Mock<IDataSourceResolver> CreateResolverMock()
    {
        var resolverMock = new Mock<IDataSourceResolver>();
        resolverMock
            .Setup(r => r.ResolveLogical(It.IsAny<DataSource>(), It.IsAny<string>()))
            .Returns((DataSource engine, string _) => DataSourceKey.Default(engine));
        return resolverMock;
    }

    /// <summary>
    /// Advances the fake clock past the startup delay in small steps, yielding a few real
    /// milliseconds after each advance so the processor's continuation can run.
    /// <see cref="FakeTimeProvider.Advance"/> only completes a Delay whose timer already
    /// exists, so the loop tolerates the startup race between <c>StartAsync</c> and the
    /// Delay registration (the same pattern as OutboxCleanupServiceTests).
    /// </summary>
    private static async Task AdvanceUntilCompletedAsync(FakeTimeProvider timeProvider, Task executeTask)
    {
        for (var i = 0; i < 100 && !executeTask.IsCompleted; i++)
        {
            timeProvider.Advance(TimeSpan.FromSeconds(5));
            await Task.Delay(TimeSpan.FromMilliseconds(10), TimeProvider.System, CancellationToken.None);
        }

        await executeTask.WaitAsync(TimeSpan.FromSeconds(5), TimeProvider.System);
    }

    [Fact]
    public async Task ExecuteAsync_CosmosDBDataSource_ExitsWithoutProcessing()
    {
        var settings = new OutboxSettings
        {
            DataSource = DataSource.CosmosDB,
        };
        var timeProvider = new FakeTimeProvider();

        var mockScopeFactory = new Mock<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>();
        var outboxSignal = new Mock<IOutboxSignal>();
        using var sut = new OutboxProcessor(
            mockScopeFactory.Object,
            NullLogger<OutboxProcessor>.Instance,
            Options.Create(settings),
            outboxSignal.Object,
            CreateEmptyRegistryMock().Object,
            CreateResolverMock().Object,
            timeProvider);

        await sut.StartAsync(CancellationToken.None);

        // Cosmos target + empty registry → no outbox sources; once the (fake) 5-second startup
        // delay elapses, the processor exits on its own.
        await AdvanceUntilCompletedAsync(timeProvider, sut.ExecuteTask!);
        await sut.StopAsync(CancellationToken.None);

        mockScopeFactory.Verify(
            f => f.CreateScope(),
            Times.Never,
            "Outbox processor should not create a DI scope when no outbox-capable data sources exist");
    }

    [Fact]
    public async Task ExecuteAsync_CancellationDuringStartupDelay_ExitsGracefully()
    {
        var settings = new OutboxSettings
        {
            DataSource = DataSource.SQLServer,
        };
        var timeProvider = new FakeTimeProvider();

        var mockScopeFactory = new Mock<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>();
        var outboxSignal = new Mock<IOutboxSignal>();
        using var sut = new OutboxProcessor(
            mockScopeFactory.Object,
            NullLogger<OutboxProcessor>.Instance,
            Options.Create(settings),
            outboxSignal.Object,
            CreateEmptyRegistryMock().Object,
            CreateResolverMock().Object,
            timeProvider);

        using var cts = new CancellationTokenSource();
        await sut.StartAsync(cts.Token);

        // Cancel while the processor is still inside the (fake, never-advanced) startup delay.
        await cts.CancelAsync();
        try
        {
            await sut.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(30), TimeProvider.System);
        }
        catch (OperationCanceledException)
        {
            // Cancellation during the startup delay surfaces as a canceled ExecuteTask — still a
            // graceful, non-processing exit.
        }

        await sut.StopAsync(CancellationToken.None);

        sut.ExecuteTask!.IsCompleted.Should().BeTrue();
        sut.ExecuteTask.IsFaulted.Should().BeFalse();
        mockScopeFactory.Verify(
            f => f.CreateScope(),
            Times.Never,
            "Outbox processor should not process messages when cancelled during startup delay");
    }

    [Fact]
    public async Task ExecuteAsync_CosmosDB_LogsDisabledMessage()
    {
        var settings = new OutboxSettings { DataSource = DataSource.CosmosDB };
        var timeProvider = new FakeTimeProvider();

        var loggedMessages = new List<string>();
        var mockLogger = new Mock<ILogger<OutboxProcessor>>();
        mockLogger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        mockLogger
            .Setup(l => l.Log(
                It.IsAny<LogLevel>(),
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()))
            .Callback(new InvocationAction(invocation =>
            {
                var formatter = (Delegate)invocation.Arguments[4];
                loggedMessages.Add((string)formatter.DynamicInvoke(invocation.Arguments[2], invocation.Arguments[3])!);
            }));

        var mockScopeFactory = new Mock<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>();
        var outboxSignal = new Mock<IOutboxSignal>();
        using var sut = new OutboxProcessor(
            mockScopeFactory.Object,
            mockLogger.Object,
            Options.Create(settings),
            outboxSignal.Object,
            CreateEmptyRegistryMock().Object,
            CreateResolverMock().Object,
            timeProvider);

        await sut.StartAsync(CancellationToken.None);
        await AdvanceUntilCompletedAsync(timeProvider, sut.ExecuteTask!);
        await sut.StopAsync(CancellationToken.None);

        loggedMessages.Should().Contain(
            "Outbox processor disabled: no relational data sources in use (Cosmos DB does not support the outbox table)");
    }
}

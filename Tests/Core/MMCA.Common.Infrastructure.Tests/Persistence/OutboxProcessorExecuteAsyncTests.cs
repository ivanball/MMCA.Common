#pragma warning disable CA2000 // Dispose objects before losing scope — BackgroundService lifecycle managed by test

using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Infrastructure.Persistence.Outbox;
using MMCA.Common.Infrastructure.Settings;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Persistence;

/// <summary>
/// Tests for <see cref="OutboxProcessor.ExecuteAsync"/> edge cases that are not covered
/// by the ProcessPendingMessagesAsync reflection-based tests in OutboxProcessorTests.
/// </summary>
public sealed class OutboxProcessorExecuteAsyncTests
{
    [Fact]
    public async Task ExecuteAsync_CosmosDBDataSource_ExitsImmediatelyWithoutProcessing()
    {
        var settings = new OutboxSettings
        {
            DataSource = DataSource.CosmosDB,
        };

        var mockScopeFactory = new Mock<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>();
        var outboxSignal1 = new Mock<MMCA.Common.Infrastructure.Persistence.Outbox.IOutboxSignal>();
        using var sut = new OutboxProcessor(
            mockScopeFactory.Object,
            NullLogger<OutboxProcessor>.Instance,
            Options.Create(settings),
            outboxSignal1.Object);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await sut.StartAsync(cts.Token);

        await Task.Delay(200, CancellationToken.None);
        await sut.StopAsync(CancellationToken.None);

        mockScopeFactory.Verify(
            f => f.CreateScope(),
            Times.Never,
            "Outbox processor should not create a DI scope when data source is CosmosDB");
    }

    [Fact]
    public async Task ExecuteAsync_CancellationDuringStartupDelay_ExitsGracefully()
    {
        var settings = new OutboxSettings
        {
            DataSource = DataSource.SQLServer,
        };

        var mockScopeFactory = new Mock<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>();
        var outboxSignal2 = new Mock<MMCA.Common.Infrastructure.Persistence.Outbox.IOutboxSignal>();
        using var sut = new OutboxProcessor(
            mockScopeFactory.Object,
            NullLogger<OutboxProcessor>.Instance,
            Options.Create(settings),
            outboxSignal2.Object);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await sut.StartAsync(cts.Token);

        await Task.Delay(500, CancellationToken.None);
        await sut.StopAsync(CancellationToken.None);

        mockScopeFactory.Verify(
            f => f.CreateScope(),
            Times.Never,
            "Outbox processor should not process messages when cancelled during startup delay");
    }

    [Fact]
    public async Task ExecuteAsync_CosmosDB_LogsDisabledMessage()
    {
        var settings = new OutboxSettings { DataSource = DataSource.CosmosDB };
        var mockLogger = new Mock<ILogger<OutboxProcessor>>();
        mockLogger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

        var mockScopeFactory = new Mock<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>();
        var outboxSignal3 = new Mock<MMCA.Common.Infrastructure.Persistence.Outbox.IOutboxSignal>();
        using var sut = new OutboxProcessor(mockScopeFactory.Object, mockLogger.Object, Options.Create(settings), outboxSignal3.Object);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await sut.StartAsync(cts.Token);
        await Task.Delay(200, CancellationToken.None);
        await sut.StopAsync(CancellationToken.None);

        mockLogger.Verify(l => l.IsEnabled(LogLevel.Information), Times.AtLeastOnce);
    }
}

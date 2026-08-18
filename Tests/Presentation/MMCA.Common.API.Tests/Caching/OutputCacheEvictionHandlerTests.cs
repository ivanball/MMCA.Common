using AwesomeAssertions;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MMCA.Common.API.Caching;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Domain.IntegrationEvents;
using Moq;

namespace MMCA.Common.API.Tests.Caching;

/// <summary>
/// Unit tests for <see cref="OutputCacheEvictionHandler"/>: every tag on the message is evicted, a
/// single tag's failure is swallowed and logged rather than rethrown (rethrowing would redeliver
/// the message and re-evict the tags that already succeeded, and would eventually dead-letter a
/// message whose only consequence is a cache entry that expires on its own), and cancellation still
/// propagates.
/// </summary>
public sealed class OutputCacheEvictionHandlerTests
{
    [Fact]
    public async Task HandleAsync_EvictsEveryTag()
    {
        var store = new Mock<IOutputCacheStore>();

        await CreateHandler(store).HandleAsync(
            new OutputCacheEvictionRequested { Tags = ["sessions", "speakers"] },
            CancellationToken.None);

        store.Verify(s => s.EvictByTagAsync("sessions", It.IsAny<CancellationToken>()), Times.Once);
        store.Verify(s => s.EvictByTagAsync("speakers", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleAsync_WithNoTags_DoesNothing()
    {
        var store = new Mock<IOutputCacheStore>(MockBehavior.Strict);

        var act = async () => await CreateHandler(store).HandleAsync(
            new OutputCacheEvictionRequested(),
            CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task HandleAsync_SkipsBlankTags(string tag)
    {
        var store = new Mock<IOutputCacheStore>(MockBehavior.Strict);

        var act = async () => await CreateHandler(store).HandleAsync(
            new OutputCacheEvictionRequested { Tags = [tag] },
            CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task HandleAsync_WhenOneTagFails_SwallowsItAndKeepsGoing()
    {
        var store = new Mock<IOutputCacheStore>();
        store.Setup(s => s.EvictByTagAsync("broken", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("store is down"));

        var logger = new RecordingLogger();

        var act = async () => await new OutputCacheEvictionHandler(store.Object, logger).HandleAsync(
            new OutputCacheEvictionRequested { Tags = ["broken", "sessions"] },
            CancellationToken.None);

        await act.Should().NotThrowAsync(
            because: "a failed eviction is a staleness window, not a lost fact worth dead-lettering");

        // One bad tag must not abandon the rest of the message.
        store.Verify(s => s.EvictByTagAsync("sessions", It.IsAny<CancellationToken>()), Times.Once);
        logger.Warnings.Should().ContainSingle().Which.Should().Contain("broken");
    }

    // Host shutdown must stay a cancellation so MassTransit sees the shutdown rather than an acked
    // message, so this exception type is deliberately NOT part of the best-effort swallow.
    [Fact]
    public async Task HandleAsync_WhenCancelled_PropagatesTheCancellation()
    {
        var store = new Mock<IOutputCacheStore>();
        store.Setup(s => s.EvictByTagAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var act = async () => await CreateHandler(store).HandleAsync(
            new OutputCacheEvictionRequested { Tags = ["sessions"] },
            CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task HandleAsync_WithNullEvent_Throws()
    {
        var act = async () => await CreateHandler(new Mock<IOutputCacheStore>()).HandleAsync(null!, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public void AddOutputCacheEvictionHandler_RegistersTheHandlerOnceHoweverOftenItIsCalled()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new Mock<IOutputCacheStore>().Object);
        services.AddLogging();

        services.AddOutputCacheEvictionHandler();
        services.AddOutputCacheEvictionHandler();

        using var provider = services.BuildServiceProvider();
        provider.GetServices<IIntegrationEventHandler<OutputCacheEvictionRequested>>()
            .Should().ContainSingle().Which.Should().BeOfType<OutputCacheEvictionHandler>();
    }

    private static OutputCacheEvictionHandler CreateHandler(Mock<IOutputCacheStore> store) =>
        new(store.Object, NullLogger<OutputCacheEvictionHandler>.Instance);

    private sealed class RecordingLogger : ILogger<OutputCacheEvictionHandler>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }
}

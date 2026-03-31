using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using MMCA.Common.Application.DomainEvents;
using MMCA.Common.Domain.DomainEvents;
using Moq;

namespace MMCA.Common.Application.Tests.DomainEvents;

public sealed class SafeDomainEventHandlerTests
{
    // ── Successful handling ──
    [Fact]
    public async Task HandleAsync_WhenHandlerSucceeds_CompletesWithoutException()
    {
        var logger = new Mock<ILogger<TestSafeDomainEventHandler>>();
        var sut = new TestSafeDomainEventHandler(logger.Object, shouldThrow: false);
        var domainEvent = new TestSafeDomainEvent("test-data");

        await FluentActions.Invoking(() => sut.HandleAsync(domainEvent))
            .Should().NotThrowAsync();

        sut.WasHandled.Should().BeTrue();
    }

    // ── Exception caught and logged ──
    [Fact]
    public async Task HandleAsync_WhenHandlerThrows_CatchesAndLogsWithoutPropagating()
    {
        var logger = new Mock<ILogger<TestSafeDomainEventHandler>>();
        var sut = new TestSafeDomainEventHandler(logger.Object, shouldThrow: true);
        var domainEvent = new TestSafeDomainEvent("test-data");

        await FluentActions.Invoking(() => sut.HandleAsync(domainEvent))
            .Should().NotThrowAsync();

        sut.WasHandled.Should().BeTrue();
        logger.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<InvalidOperationException>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    // ── OperationCanceledException is not caught ──
    [Fact]
    public async Task HandleAsync_WhenOperationCanceledException_Propagates()
    {
        var logger = new Mock<ILogger<TestSafeDomainEventHandler>>();
        var sut = new TestSafeDomainEventHandler(logger.Object, throwCancellation: true);
        var domainEvent = new TestSafeDomainEvent("test-data");

        await FluentActions.Invoking(() => sut.HandleAsync(domainEvent))
            .Should().ThrowAsync<OperationCanceledException>();
    }
}

// ── Test helpers ──
public sealed record TestSafeDomainEvent(string Data) : BaseDomainEvent;

public sealed class TestSafeDomainEventHandler : SafeDomainEventHandler<TestSafeDomainEvent>
{
    private readonly bool _shouldThrow;
    private readonly bool _throwCancellation;

    public bool WasHandled { get; private set; }

    public TestSafeDomainEventHandler(ILogger logger, bool shouldThrow = false, bool throwCancellation = false)
        : base(logger)
    {
        _shouldThrow = shouldThrow;
        _throwCancellation = throwCancellation;
    }

    protected override Task HandleSafelyAsync(TestSafeDomainEvent domainEvent, CancellationToken cancellationToken)
    {
        WasHandled = true;

        if (_throwCancellation)
        {
            throw new OperationCanceledException();
        }

        if (_shouldThrow)
        {
            throw new InvalidOperationException("Handler failed");
        }

        return Task.CompletedTask;
    }
}

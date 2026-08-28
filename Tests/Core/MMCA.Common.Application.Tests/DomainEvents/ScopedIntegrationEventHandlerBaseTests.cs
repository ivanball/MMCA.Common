using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MMCA.Common.Application.DomainEvents;
using MMCA.Common.Domain.DomainEvents;

namespace MMCA.Common.Application.Tests.DomainEvents;

/// <summary>
/// The base opens one DI scope per delivery (integration event handlers are singletons and cannot
/// hold a scoped service), hands the subclass that scope's provider, disposes it on every path,
/// and logs a failure exactly once before letting it propagate so the delivery mechanism can
/// redeliver. Cancellation passes through unlogged: host shutdown is not a delivery failure.
/// </summary>
public sealed class ScopedIntegrationEventHandlerBaseTests
{
    // ── The scope is opened, its provider is handed to the subclass, and it is disposed ──
    [Fact]
    public async Task HandleAsync_WhenHandlerSucceeds_ResolvesFromAScopeAndDisposesIt()
    {
        await using ServiceProvider provider = BuildProvider();
        var logger = new RecordingLogger();
        var sut = new TestScopedIntegrationEventHandler(provider.GetRequiredService<IServiceScopeFactory>(), logger);

        await sut.HandleAsync(new TestIntegrationEvent("payload"));

        sut.ResolvedService.Should().NotBeNull("the subclass resolves scoped services from the scope the base opened");
        sut.ResolvedService!.IsDisposed.Should().BeTrue("the scope is disposed once the handler body returns");
        logger.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleAsync_ForTwoDeliveries_OpensASeparateScopeEachTime()
    {
        await using ServiceProvider provider = BuildProvider();
        var sut = new TestScopedIntegrationEventHandler(provider.GetRequiredService<IServiceScopeFactory>(), new RecordingLogger());

        await sut.HandleAsync(new TestIntegrationEvent("first"));
        ScopedProbe? first = sut.ResolvedService;

        await sut.HandleAsync(new TestIntegrationEvent("second"));

        sut.ResolvedService.Should().NotBeSameAs(first, "a singleton handler must not reuse one scope across deliveries");
    }

    [Fact]
    public async Task HandleAsync_WithNullEvent_ThrowsArgumentNullException()
    {
        await using ServiceProvider provider = BuildProvider();
        var sut = new TestScopedIntegrationEventHandler(provider.GetRequiredService<IServiceScopeFactory>(), new RecordingLogger());

        await FluentActions.Invoking(() => sut.HandleAsync(null!))
            .Should().ThrowAsync<ArgumentNullException>();
    }

    // ── Exception logged AND propagated ──
    [Fact]
    public async Task HandleAsync_WhenHandlerThrows_LogsAndPropagates()
    {
        await using ServiceProvider provider = BuildProvider();
        var logger = new RecordingLogger();
        var sut = new TestScopedIntegrationEventHandler(
            provider.GetRequiredService<IServiceScopeFactory>(), logger, shouldThrow: true);

        (await FluentActions.Invoking(() => sut.HandleAsync(new TestIntegrationEvent("payload")))
                .Should().ThrowAsync<InvalidOperationException>(
                    "the delivery mechanism, not the handler, decides what happens to a failed event"))
            .WithMessage("Handler failed");

        logger.Errors.Should().ContainSingle()
            .Which.Should().BeOfType<InvalidOperationException>();
    }

    [Fact]
    public async Task HandleAsync_WhenHandlerThrows_DisposesTheScope()
    {
        await using ServiceProvider provider = BuildProvider();
        var sut = new TestScopedIntegrationEventHandler(
            provider.GetRequiredService<IServiceScopeFactory>(), new RecordingLogger(), shouldThrow: true);

        await FluentActions.Invoking(() => sut.HandleAsync(new TestIntegrationEvent("payload")))
            .Should().ThrowAsync<InvalidOperationException>();

        sut.ResolvedService!.IsDisposed.Should().BeTrue("a failed delivery must not leak the scope it opened");
    }

    [Fact]
    public async Task HandleAsync_WhenHandlerThrows_WritesTheLogBeforeTheCallerSeesTheException()
    {
        await using ServiceProvider provider = BuildProvider();
        var logger = new RecordingLogger();
        var sut = new TestScopedIntegrationEventHandler(
            provider.GetRequiredService<IServiceScopeFactory>(), logger, shouldThrow: true);

        try
        {
            await sut.HandleAsync(new TestIntegrationEvent("payload"));
        }
        catch (InvalidOperationException)
        {
            logger.Sequence.Add("caught");
        }

        logger.Sequence.Should().Equal(
            ["logged", "caught"],
            "an exception filter logs on the first pass, so the handler context is recorded even if an outer frame rethrows or wraps");
    }

    // ── OperationCanceledException still passes straight through, unlogged ──
    [Fact]
    public async Task HandleAsync_WhenOperationCanceledException_PropagatesWithoutLogging()
    {
        await using ServiceProvider provider = BuildProvider();
        var logger = new RecordingLogger();
        var sut = new TestScopedIntegrationEventHandler(
            provider.GetRequiredService<IServiceScopeFactory>(), logger, throwCancellation: true);

        await FluentActions.Invoking(() => sut.HandleAsync(new TestIntegrationEvent("payload")))
            .Should().ThrowAsync<OperationCanceledException>();

        logger.Errors.Should().BeEmpty("host shutdown is not a delivery failure and must not be logged as one");
        logger.Sequence.Should().BeEmpty();
    }

    // ── The log line is an extension point ──
    [Fact]
    public async Task HandleAsync_WhenSubclassOverridesTheLog_UsesTheOverride()
    {
        await using ServiceProvider provider = BuildProvider();
        var logger = new RecordingLogger();
        var sut = new CustomLoggingIntegrationEventHandler(provider.GetRequiredService<IServiceScopeFactory>(), logger);

        await FluentActions.Invoking(() => sut.HandleAsync(new TestIntegrationEvent("payload")))
            .Should().ThrowAsync<InvalidOperationException>();

        sut.LoggedPayload.Should().Be("payload", "the override receives the event so it can log the event's own identifiers");
        logger.Errors.Should().BeEmpty("the override replaced the default log line entirely");
    }

    [Fact]
    public async Task HandleAsync_PassesTheCancellationTokenThrough()
    {
        await using ServiceProvider provider = BuildProvider();
        var sut = new TestScopedIntegrationEventHandler(provider.GetRequiredService<IServiceScopeFactory>(), new RecordingLogger());
        using var cts = new CancellationTokenSource();

        await sut.HandleAsync(new TestIntegrationEvent("payload"), cts.Token);

        sut.ObservedToken.Should().Be(cts.Token);
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddScoped<ScopedProbe>();

        return services.BuildServiceProvider();
    }
}

// ── Test helpers ──
public sealed record TestIntegrationEvent(string Payload) : BaseIntegrationEvent;

/// <summary>Scoped service that records whether the scope that produced it was disposed.</summary>
public sealed class ScopedProbe : IDisposable
{
    public bool IsDisposed { get; private set; }

    public void Dispose() => IsDisposed = true;
}

public sealed class TestScopedIntegrationEventHandler : ScopedIntegrationEventHandlerBase<TestIntegrationEvent>
{
    private readonly bool _shouldThrow;
    private readonly bool _throwCancellation;

    public TestScopedIntegrationEventHandler(
        IServiceScopeFactory scopeFactory,
        ILogger logger,
        bool shouldThrow = false,
        bool throwCancellation = false)
        : base(scopeFactory, logger)
    {
        _shouldThrow = shouldThrow;
        _throwCancellation = throwCancellation;
    }

    public ScopedProbe? ResolvedService { get; private set; }

    public CancellationToken ObservedToken { get; private set; }

    protected override Task HandleScopedAsync(
        TestIntegrationEvent integrationEvent,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        ResolvedService = services.GetRequiredService<ScopedProbe>();
        ObservedToken = cancellationToken;

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

public sealed class CustomLoggingIntegrationEventHandler(IServiceScopeFactory scopeFactory, ILogger logger)
    : ScopedIntegrationEventHandlerBase<TestIntegrationEvent>(scopeFactory, logger)
{
    public string? LoggedPayload { get; private set; }

    protected override Task HandleScopedAsync(
        TestIntegrationEvent integrationEvent,
        IServiceProvider services,
        CancellationToken cancellationToken)
        => throw new InvalidOperationException("Handler failed");

    protected override void LogHandlerFailure(Exception exception, TestIntegrationEvent integrationEvent) =>
        LoggedPayload = integrationEvent.Payload;
}

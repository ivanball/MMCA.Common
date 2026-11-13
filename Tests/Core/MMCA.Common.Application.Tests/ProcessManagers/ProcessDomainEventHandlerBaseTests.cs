using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Application.InternalCommands;
using MMCA.Common.Application.ProcessManagers;
using MMCA.Common.Domain.DomainEvents;
using MMCA.Common.Domain.ProcessManagers;
using MMCA.Common.Shared.Abstractions;
using Moq;

namespace MMCA.Common.Application.Tests.ProcessManagers;

/// <summary>
/// Tests for <see cref="ProcessDomainEventHandlerBase{TDomainEvent, TProcess, TProcessId}"/> (design
/// item 36, rules 12 to 16): one scope and one transaction per event, the process loaded tracked inside
/// the transaction by the correlated id, a missing process skipping the step, the step owning every save,
/// and a failed step surfacing through <c>OnStepFailed</c>, whose default throws so the outbox redelivers.
/// </summary>
public sealed class ProcessDomainEventHandlerBaseTests
{
    private const int ProcessId = 11;

    private readonly List<string> _log = [];
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<IRepository<StepTestProcess, int>> _repository = new();
    private Result? _transactionResult;

    public ProcessDomainEventHandlerBaseTests()
    {
        _unitOfWork.Setup(u => u.GetRepository<StepTestProcess, int>()).Returns(_repository.Object);
        _unitOfWork
            .Setup(u => u.ExecuteInTransactionAsync(It.IsAny<Func<CancellationToken, Task<Result>>>(), It.IsAny<CancellationToken>()))
            .Returns<Func<CancellationToken, Task<Result>>, CancellationToken>(
                async (operation, token) =>
                {
                    _log.Add("transaction-begin");
                    var result = await operation(token);
                    _transactionResult = result;
                    _log.Add(result.IsSuccess ? "transaction-commit" : "transaction-rollback");
                    return result;
                });
    }

    [Fact]
    public async Task NullEvent_Throws()
    {
        await using var provider = BuildProvider();
        var handler = new TestStepHandler(provider.GetRequiredService<IServiceScopeFactory>(), _log);

        var act = () => handler.HandleAsync(null!, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task OnStepStarting_BeforeLoad()
    {
        ReturnsProcess(new StepTestProcess { Id = ProcessId });
        await using var provider = BuildProvider();
        var handler = new TestStepHandler(provider.GetRequiredService<IServiceScopeFactory>(), _log);

        await handler.HandleAsync(new WidgetShipped(ProcessId), TestContext.Current.CancellationToken);

        _log.Should().Equal(
            ["starting", "transaction-begin", "load", "step", "transaction-commit"],
            "OnStepStarting runs first, then the process is loaded and stepped inside the one transaction");
        handler.Starting.Should().ContainSingle().Which.WidgetId.Should().Be(ProcessId);
    }

    [Fact]
    public async Task OneScopeOneTransaction()
    {
        ReturnsProcess(new StepTestProcess { Id = ProcessId });
        await using var provider = BuildProvider();
        var scopeFactory = new CountingScopeFactory(provider.GetRequiredService<IServiceScopeFactory>());
        var handler = new TestStepHandler(scopeFactory, _log);
        using var cts = new CancellationTokenSource();

        await handler.HandleAsync(new WidgetShipped(ProcessId), cts.Token);

        scopeFactory.CreatedScopes.Should().Be(1, "the step's services and the unit of work share one scope per event");
        _unitOfWork.Verify(
            u => u.ExecuteInTransactionAsync(It.IsAny<Func<CancellationToken, Task<Result>>>(), cts.Token),
            Times.Once);
        handler.ObservedTokens.Should().ContainSingle().Which.Should().Be(cts.Token);
    }

    [Fact]
    public async Task Missing_CallsOnProcessMissing_SkipsStep()
    {
        _repository
            .Setup(r => r.GetByIdAsync(ProcessId, It.IsAny<IEnumerable<string>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback(() => _log.Add("load"))
            .ReturnsAsync((StepTestProcess?)null);
        await using var provider = BuildProvider();
        var handler = new TestStepHandler(provider.GetRequiredService<IServiceScopeFactory>(), _log);
        var domainEvent = new WidgetShipped(ProcessId);

        await handler.HandleAsync(domainEvent, TestContext.Current.CancellationToken);

        handler.Missing.Should().ContainSingle().Which.Should().BeSameAs(domainEvent);
        handler.Steps.Should().BeEmpty("there is no process to step");
        handler.Failed.Should().BeEmpty("a missing process is not a failed step");
        _transactionResult.Should().NotBeNull();
        _transactionResult!.IsSuccess.Should().BeTrue("the delegate returns Success when the process is missing");
    }

    [Fact]
    public async Task StepGetsTrackedProcessWithIncludes()
    {
        var process = new StepTestProcess { Id = ProcessId };
        ReturnsProcess(process);
        await using var provider = BuildProvider();
        // The scope lives only for the duration of the step (the base disposes it when the event is
        // handled), so what the step can resolve from it is captured while the step runs.
        IUnitOfWork? resolvedInsideTheStep = null;
        var handler = new TestStepHandler(provider.GetRequiredService<IServiceScopeFactory>(), _log)
        {
            Step = (_, context) =>
            {
                resolvedInsideTheStep = context.Services.GetRequiredService<IUnitOfWork>();
                return Task.FromResult(Result.Success());
            },
        };
        var domainEvent = new WidgetShipped(ProcessId);
        using var cts = new CancellationTokenSource();

        await handler.HandleAsync(domainEvent, cts.Token);

        _repository.Verify(
            r => r.GetByIdAsync(
                ProcessId,
                It.Is<IEnumerable<string>>(includes => includes.SequenceEqual(new[] { TestStepHandler.LinesInclude })),
                true,
                It.IsAny<CancellationToken>()),
            Times.Once,
            "the step mutates the process, so it is loaded tracked with the subclass's includes by the correlated id");
        handler.Correlated.Should().ContainSingle().Which.Should().BeSameAs(domainEvent);

        var step = handler.Steps.Should().ContainSingle().Which;
        step.Process.Should().BeSameAs(process);
        step.DomainEvent.Should().BeSameAs(domainEvent);
        step.Context.UnitOfWork.Should().BeSameAs(_unitOfWork.Object, "the step saves through the scope's unit of work");
        step.Context.Services.Should().NotBeSameAs(provider, "the step resolves from the scope the base opened, not the root");
        resolvedInsideTheStep.Should().BeSameAs(step.Context.UnitOfWork, "the scope hands out the same unit of work the context carries");
    }

    [Fact]
    public async Task FailedStep_DefaultThrowsProcessStepFailedExceptionWithErrors()
    {
        IReadOnlyList<Error> errors = [Error.Failure("Inventory.RestoreFailed", "The stock could not be restored.")];
        ReturnsProcess(new StepTestProcess { Id = ProcessId });
        await using var provider = BuildProvider();
        var handler = new TestStepHandler(provider.GetRequiredService<IServiceScopeFactory>(), _log)
        {
            Step = (_, _) => Task.FromResult(Result.Failure(errors)),
        };

        var act = () => handler.HandleAsync(new WidgetShipped(ProcessId), TestContext.Current.CancellationToken);

        var exception = (await act.Should().ThrowAsync<ProcessStepFailedException>(
            "the outbox redelivers an event whose handler throws")).Which;
        exception.Errors.Should().Equal(errors);
        exception.ProcessType.Should().Contain(nameof(StepTestProcess));
        exception.ProcessId.Should().Be(ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        handler.Failed.Should().ContainSingle().Which.Should().Equal(errors);
        _log.Should().ContainInOrder(
            ["transaction-rollback", "failed"],
            "the unit is rolled back first and OnStepFailed runs after the transaction");
    }

    [Fact]
    public async Task FailedStep_OverrideReturnsNormally()
    {
        IReadOnlyList<Error> errors = [Error.Failure("Inventory.RestoreFailed", "The stock could not be restored.")];
        ReturnsProcess(new StepTestProcess { Id = ProcessId });
        await using var provider = BuildProvider();
        var handler = new TestStepHandler(provider.GetRequiredService<IServiceScopeFactory>(), _log)
        {
            SwallowFailures = true,
            Step = (_, _) => Task.FromResult(Result.Failure(errors)),
        };

        var act = () => handler.HandleAsync(new WidgetShipped(ProcessId), TestContext.Current.CancellationToken);

        await act.Should().NotThrowAsync("an override that returns normally keeps the event handled");
        handler.Failed.Should().ContainSingle().Which.Should().Equal(errors);
        _transactionResult!.IsFailure.Should().BeTrue("the failed delegate result is what rolls the unit back");
    }

    [Fact]
    public async Task StepException_Propagates()
    {
        var boom = new InvalidOperationException("Step failed.");
        ReturnsProcess(new StepTestProcess { Id = ProcessId });
        await using var provider = BuildProvider();
        var handler = new TestStepHandler(provider.GetRequiredService<IServiceScopeFactory>(), _log)
        {
            Step = (_, _) => throw boom,
        };

        var act = () => handler.HandleAsync(new WidgetShipped(ProcessId), TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(boom, "the exception is not wrapped");
        handler.Failed.Should().BeEmpty("OnStepFailed is for a failed Result, not for an exception");
    }

    [Fact]
    public async Task StepCancellation_Propagates()
    {
        ReturnsProcess(new StepTestProcess { Id = ProcessId });
        await using var provider = BuildProvider();
        var handler = new TestStepHandler(provider.GetRequiredService<IServiceScopeFactory>(), _log)
        {
            Step = (_, _) => throw new OperationCanceledException(),
        };

        var act = () => handler.HandleAsync(new WidgetShipped(ProcessId), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<OperationCanceledException>("cancellation is not caught");
        handler.Failed.Should().BeEmpty();
    }

    [Fact]
    public async Task BaseNeverSaves()
    {
        ReturnsProcess(new StepTestProcess { Id = ProcessId });
        await using var provider = BuildProvider();
        var handler = new TestStepHandler(provider.GetRequiredService<IServiceScopeFactory>(), _log);

        await handler.HandleAsync(new WidgetShipped(ProcessId), TestContext.Current.CancellationToken);

        handler.Steps.Should().ContainSingle();
        _unitOfWork.Verify(
            u => u.SaveChangesAsync(It.IsAny<CancellationToken>()),
            Times.Never,
            "the step owns its saves; the base only opens the scope and the transaction");
    }

    [Fact]
    public async Task BaseNeverSaves_WhenTheProcessIsMissing()
    {
        _repository
            .Setup(r => r.GetByIdAsync(ProcessId, It.IsAny<IEnumerable<string>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((StepTestProcess?)null);
        await using var provider = BuildProvider();
        var handler = new TestStepHandler(provider.GetRequiredService<IServiceScopeFactory>(), _log);

        await handler.HandleAsync(new WidgetShipped(ProcessId), TestContext.Current.CancellationToken);

        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ContextTimeoutsNullWithoutAddProcessManagers()
    {
        ReturnsProcess(new StepTestProcess { Id = ProcessId });
        await using var provider = BuildProvider();
        var handler = new TestStepHandler(provider.GetRequiredService<IServiceScopeFactory>(), _log);

        await handler.HandleAsync(new WidgetShipped(ProcessId), TestContext.Current.CancellationToken);

        handler.Steps.Should().ContainSingle().Which.Context.Timeouts.Should().BeNull(
            "a host that never called AddProcessManagers has no timeout scheduler, and the step must cope");
    }

    [Fact]
    public async Task ContextTimeoutsResolvedWithAddProcessManagers()
    {
        ReturnsProcess(new StepTestProcess { Id = ProcessId });
        await using var provider = BuildProvider(withProcessManagers: true);
        var handler = new TestStepHandler(provider.GetRequiredService<IServiceScopeFactory>(), _log);

        await handler.HandleAsync(new WidgetShipped(ProcessId), TestContext.Current.CancellationToken);

        handler.Steps.Should().ContainSingle().Which.Context.Timeouts.Should().BeOfType<ProcessTimeoutScheduler>();
    }

    [Fact]
    public void ProcessStepFailedException_HasTheStandardConstructors()
    {
        var inner = new InvalidOperationException("inner");

        new ProcessStepFailedException().Should().BeAssignableTo<Exception>();
        new ProcessStepFailedException("message").Message.Should().Be("message");
        new ProcessStepFailedException("message", inner).InnerException.Should().BeSameAs(inner);
    }

    private ServiceProvider BuildProvider(bool withProcessManagers = false)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => _unitOfWork.Object);

        if (withProcessManagers)
        {
            services.AddSingleton(Mock.Of<IInternalCommandScheduler>());
            services.AddProcessManagers();
        }

        return services.BuildServiceProvider();
    }

    private void ReturnsProcess(StepTestProcess process) =>
        _repository
            .Setup(r => r.GetByIdAsync(ProcessId, It.IsAny<IEnumerable<string>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback(() => _log.Add("load"))
            .ReturnsAsync(process);
}

// -- Test doubles (public: Moq proxies IRepository<StepTestProcess, int>) --

/// <summary>The domain event that drives the test process; WidgetId correlates it.</summary>
public sealed record WidgetShipped(int WidgetId) : BaseDomainEvent;

/// <summary>A process aggregate with every default of the base.</summary>
public sealed class StepTestProcess : ProcessManagerAggregateRoot<int>;

/// <summary>One step invocation as the base handed it over.</summary>
public sealed record StepInvocation(StepTestProcess Process, WidgetShipped DomainEvent, ProcessStepContext Context);

/// <summary>
/// A derived step handler that observes the base only through its declared protected hooks. The step
/// body is configurable; OnStepFailed records the errors and, unless SwallowFailures is set, defers to
/// the base default (which throws).
/// </summary>
public sealed class TestStepHandler(IServiceScopeFactory serviceScopeFactory, List<string> log)
    : ProcessDomainEventHandlerBase<WidgetShipped, StepTestProcess, int>(serviceScopeFactory)
{
    public const string LinesInclude = "Lines";

    public Func<StepTestProcess, ProcessStepContext, Task<Result>> Step { get; init; } =
        (_, _) => Task.FromResult(Result.Success());

    public bool SwallowFailures { get; init; }

    public List<WidgetShipped> Starting { get; } = [];

    public List<WidgetShipped> Correlated { get; } = [];

    public List<WidgetShipped> Missing { get; } = [];

    public List<StepInvocation> Steps { get; } = [];

    public List<CancellationToken> ObservedTokens { get; } = [];

    public List<IReadOnlyList<Error>> Failed { get; } = [];

    protected override IEnumerable<string> Includes => [LinesInclude];

    protected override int CorrelateProcessId(WidgetShipped domainEvent)
    {
        Correlated.Add(domainEvent);
        return domainEvent.WidgetId;
    }

    protected override Task<Result> HandleStepAsync(
        StepTestProcess process,
        WidgetShipped domainEvent,
        ProcessStepContext context,
        CancellationToken cancellationToken)
    {
        log.Add("step");
        Steps.Add(new StepInvocation(process, domainEvent, context));
        ObservedTokens.Add(cancellationToken);
        return Step(process, context);
    }

    protected override void OnStepStarting(WidgetShipped domainEvent)
    {
        log.Add("starting");
        Starting.Add(domainEvent);
    }

    protected override void OnProcessMissing(WidgetShipped domainEvent)
    {
        log.Add("missing");
        Missing.Add(domainEvent);
    }

    protected override void OnStepFailed(WidgetShipped domainEvent, IReadOnlyList<Error> errors)
    {
        log.Add("failed");
        Failed.Add(errors);

        if (!SwallowFailures)
        {
            base.OnStepFailed(domainEvent, errors);
        }
    }
}

/// <summary>Counts the scopes the base opens; CreateAsyncScope on a factory goes through CreateScope.</summary>
public sealed class CountingScopeFactory(IServiceScopeFactory inner) : IServiceScopeFactory
{
    public int CreatedScopes { get; private set; }

    public IServiceScope CreateScope()
    {
        CreatedScopes++;
        return inner.CreateScope();
    }
}

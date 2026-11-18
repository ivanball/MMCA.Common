using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Tests.Fakes.Billing.Domain;
using MMCA.Common.Application.UseCases.Contracts;
using MMCA.Common.Application.UseCases.Decorators;
using MMCA.Common.Shared.Abstractions;
using Moq;

namespace MMCA.Common.Application.Tests.Decorators;

public sealed class LoggingCommandDecoratorTests : LoggingDecoratorTestsBase<Result>
{
    protected override string RequestNameScopeKey => "CommandName";

    protected override string NoModuleRequestName => nameof(TestLoggingCommand);

    protected override string BillingModuleRequestName => nameof(BillingFakeCommand);

    protected override Result SuccessResult => Result.Success();

    // ── Command only: a failure Result passes through untouched ──
    [Fact]
    public async Task HandleAsync_WhenInnerReturnsFailure_ReturnsFailureResult()
    {
        var handle = CreateSut(
            RequestKind.NoModule,
            () => Task.FromResult(Result.Failure(Error.Validation("Test.Error", "something went wrong"))));

        var result = await handle();

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle().Which.Code.Should().Be("Test.Error");
    }

    protected override Func<Task<Result>> CreateSut(
        RequestKind kind,
        Func<Task<Result>> inner,
        ICorrelationContext correlationContext,
        ILogger sink) => kind switch
        {
            RequestKind.NoModule => Wrap(new TestLoggingCommand(), inner, correlationContext, sink),
            RequestKind.BillingModule => Wrap(new BillingFakeCommand(), inner, correlationContext, sink),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };

    private static Func<Task<Result>> Wrap<TCommand>(
        TCommand command,
        Func<Task<Result>> inner,
        ICorrelationContext correlationContext,
        ILogger sink)
        where TCommand : class
    {
        var handler = new Mock<ICommandHandler<TCommand, Result>>();
        handler.Setup(x => x.HandleAsync(It.IsAny<TCommand>(), It.IsAny<CancellationToken>()))
            .Returns(() => inner());

        var sut = new LoggingCommandDecorator<TCommand, Result>(
            handler.Object,
            correlationContext,
            new ForwardingLogger<LoggingCommandDecorator<TCommand, Result>>(sink));

        return () => sut.HandleAsync(command);
    }
}

// ── Test type (must be public for Moq DynamicProxy) ──
public sealed record TestLoggingCommand;

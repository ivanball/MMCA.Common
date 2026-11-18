using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Tests.Fakes.Billing.Domain;
using MMCA.Common.Application.UseCases.Contracts;
using MMCA.Common.Application.UseCases.Decorators;
using MMCA.Common.Shared.Abstractions;
using Moq;

namespace MMCA.Common.Application.Tests.Decorators;

public sealed class LoggingQueryDecoratorTests : LoggingDecoratorTestsBase<Result<string>>
{
    protected override string RequestNameScopeKey => "QueryName";

    protected override string NoModuleRequestName => nameof(TestLoggingQuery);

    protected override string BillingModuleRequestName => nameof(BillingFakeQuery);

    protected override Result<string> SuccessResult => Result.Success("data");

    protected override void AssertPassedThrough(Result<string> result)
    {
        base.AssertPassedThrough(result);
        result.Value.Should().Be("data");
    }

    protected override Func<Task<Result<string>>> CreateSut(
        RequestKind kind,
        Func<Task<Result<string>>> inner,
        ICorrelationContext correlationContext,
        ILogger sink) => kind switch
        {
            RequestKind.NoModule => Wrap(new TestLoggingQuery(), inner, correlationContext, sink),
            RequestKind.BillingModule => Wrap(new BillingFakeQuery(), inner, correlationContext, sink),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };

    private static Func<Task<Result<string>>> Wrap<TQuery>(
        TQuery query,
        Func<Task<Result<string>>> inner,
        ICorrelationContext correlationContext,
        ILogger sink)
        where TQuery : class
    {
        var handler = new Mock<IQueryHandler<TQuery, Result<string>>>();
        handler.Setup(x => x.HandleAsync(It.IsAny<TQuery>(), It.IsAny<CancellationToken>()))
            .Returns(() => inner());

        var sut = new LoggingQueryDecorator<TQuery, Result<string>>(
            handler.Object,
            correlationContext,
            new ForwardingLogger<LoggingQueryDecorator<TQuery, Result<string>>>(sink));

        return () => sut.HandleAsync(query);
    }
}

// ── Test type (must be public for Moq DynamicProxy) ──
public sealed record TestLoggingQuery;

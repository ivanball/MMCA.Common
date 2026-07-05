using System.Reflection;
using AwesomeAssertions;
using MMCA.Common.Application.UseCases.Decorators;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Application.Tests.Decorators;

/// <summary>
/// Tests for <c>ResultFailureFactory</c>, the cached-delegate builder decorators use to
/// short-circuit the pipeline with a failure result. The factory is internal and
/// Application.Tests has no InternalsVisibleTo (same situation as
/// <see cref="NavigationMetadataTests"/>), so it is reached via reflection through a public
/// anchor type in the same assembly.
/// </summary>
public sealed class ResultFailureFactoryTests
{
    private static readonly Type FactoryType = typeof(LoggingCommandDecorator<,>).Assembly
        .GetType("MMCA.Common.Application.UseCases.Decorators.ResultFailureFactory", throwOnError: true)!;

    private static readonly MethodInfo BuildMethod =
        FactoryType.GetMethod("Build", BindingFlags.Static | BindingFlags.NonPublic)!;

    private static Func<IEnumerable<Error>, TResult> Build<TResult>() =>
        (Func<IEnumerable<Error>, TResult>)BuildMethod
            .MakeGenericMethod(typeof(TResult))
            .Invoke(null, [])!;

    // ── Non-generic Result ──
    [Fact]
    public void Build_ForNonGenericResult_CreatesFailureCarryingErrorsInOrder()
    {
        var factory = Build<Result>();
        Error[] errors =
        [
            Error.Validation("Test.First", "first"),
            Error.Conflict("Test.Second", "second"),
        ];

        Result result = factory(errors);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().Equal(errors);
    }

    // ── Generic Result<T> ──
    [Fact]
    public void Build_ForGenericResult_CreatesTypedFailureCarryingErrors()
    {
        var factory = Build<Result<int>>();
        Error[] errors = [Error.NotFoundError("Test.Missing", "not found")];

        Result<int> result = factory(errors);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle().Which.Code.Should().Be("Test.Missing");
    }

    // ── Delegate reuse (the decorators cache it) ──
    [Fact]
    public void Build_ReturnedDelegate_IsReusableAcrossIndependentErrorSets()
    {
        var factory = Build<Result<string>>();

        Result<string> first = factory([Error.Validation("Test.A", "a")]);
        Result<string> second = factory([Error.Conflict("Test.B", "b")]);

        first.Errors.Should().ContainSingle().Which.Code.Should().Be("Test.A");
        second.Errors.Should().ContainSingle().Which.Code.Should().Be("Test.B");
    }

    // ── Unsupported result types fail fast ──
    [Fact]
    public void Build_ForUnsupportedResultType_ThrowsInvalidOperation()
    {
        var act = () => Build<string>();

        // Reflection wraps the factory's guard exception in TargetInvocationException.
        act.Should().Throw<TargetInvocationException>()
            .WithInnerException<InvalidOperationException>()
            .WithMessage("*does not support TResult type*System.String*");
    }
}

using AwesomeAssertions;
using MMCA.Common.Grpc.Exceptions;
using MMCA.Common.Shared.Abstractions;
using Xunit;

namespace MMCA.Common.Grpc.Tests;

/// <summary>
/// Verifies <see cref="ResultFailureException"/> round-trips <see cref="Error"/> collections
/// (message built from every code/message pair, errors preserved for the interceptor to
/// translate into trailers) and that the CA1032 convenience constructors carry no errors.
/// </summary>
public sealed class ResultFailureExceptionTests
{
    [Fact]
    public void Ctor_Parameterless_HasDefaultMessageAndNoErrors()
    {
        var sut = new ResultFailureException();

        sut.Message.Should().Be("Result failure");
        sut.Errors.Should().BeEmpty();
    }

    [Fact]
    public void Ctor_WithMessage_SetsMessageAndNoErrors()
    {
        var sut = new ResultFailureException("custom message");

        sut.Message.Should().Be("custom message");
        sut.Errors.Should().BeEmpty();
        sut.InnerException.Should().BeNull();
    }

    [Fact]
    public void Ctor_WithMessageAndInnerException_WrapsInnerAndCarriesNoErrors()
    {
        var inner = new InvalidOperationException("root cause");

        var sut = new ResultFailureException("outer message", inner);

        sut.Message.Should().Be("outer message");
        sut.InnerException.Should().BeSameAs(inner);
        sut.Errors.Should().BeEmpty();
    }

    [Fact]
    public void Ctor_WithErrors_BuildsMessageFromEveryCodeAndMessagePair()
    {
        IReadOnlyList<Error> errors =
        [
            Error.Validation("Test.First", "first message"),
            Error.Conflict("Test.Second", "second message"),
        ];

        var sut = new ResultFailureException(errors);

        sut.Message.Should().Be("Test.First: first message; Test.Second: second message");
        sut.Errors.Should().BeSameAs(errors, "the interceptor reads the original errors to build trailers");
    }

    [Fact]
    public void Ctor_WithEmptyErrorList_FallsBackToDefaultMessage()
    {
        IReadOnlyList<Error> errors = [];

        var sut = new ResultFailureException(errors);

        sut.Message.Should().Be("Result failure");
        sut.Errors.Should().BeEmpty();
    }
}

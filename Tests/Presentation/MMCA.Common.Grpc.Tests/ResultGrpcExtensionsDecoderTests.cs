using AwesomeAssertions;
using Grpc.Core;
using MMCA.Common.Shared.Abstractions;
using Xunit;

namespace MMCA.Common.Grpc.Tests;

/// <summary>
/// The decoder half of the shipped trailer contract: <c>Metadata.ToErrors()</c> and
/// <c>RpcException.ToResult()</c> read back exactly what <c>ToRpcException</c> wrote, so a
/// <see cref="Result"/> failure survives a gRPC hop unchanged. A transport fault that carries no
/// structured trailers still reaches the caller as a failure rather than an exception.
/// </summary>
public sealed class ResultGrpcExtensionsDecoderTests
{
    // ── Round trip: encode then decode reproduces the original errors ──
    [Fact]
    public void ToErrors_AfterToRpcException_ReproducesEveryErrorExactly()
    {
        // Arrange
        IReadOnlyList<Error> original =
        [
            Error.Conflict("Test.Conflict", "Already exists", source: "TestService", target: "Name"),
            Error.Validation("Test.Validation", "Bad input"),
            Error.Unexpected("Test.Unexpected", "Boom", source: "Inner"),
        ];

        // Act
        var encoded = original.ToRpcException();
        IReadOnlyList<Error> decoded = encoded.Trailers.ToErrors();

        // Assert
        decoded.Should().BeEquivalentTo(original, options => options.WithStrictOrdering());
    }

    [Theory]
    [InlineData(ErrorType.Validation)]
    [InlineData(ErrorType.Invariant)]
    [InlineData(ErrorType.NotFound)]
    [InlineData(ErrorType.Conflict)]
    [InlineData(ErrorType.Unauthorized)]
    [InlineData(ErrorType.Forbidden)]
    [InlineData(ErrorType.UnprocessableEntity)]
    [InlineData(ErrorType.Failure)]
    [InlineData(ErrorType.Unexpected)]
    public void ToErrors_RoundTripsEveryErrorType(ErrorType errorType)
    {
        // Arrange
        IReadOnlyList<Error> original = [new Error("Test.Code", "Test message", errorType, "Source", "Target")];

        // Act
        IReadOnlyList<Error> decoded = original.ToRpcException().Trailers.ToErrors();

        // Assert
        decoded.Should().ContainSingle();
        decoded[0].Type.Should().Be(errorType);
        decoded[0].Code.Should().Be("Test.Code");
        decoded[0].Message.Should().Be("Test message");
        decoded[0].Source.Should().Be("Source");
        decoded[0].Target.Should().Be("Target");
    }

    [Fact]
    public void ToErrors_WhenSourceAndTargetWereAbsent_DecodesThemAsNull()
    {
        // Arrange: the encoder omits an empty source/target entirely
        IReadOnlyList<Error> original = [Error.NotFoundError("Test.NotFound", "Item not found")];

        // Act
        IReadOnlyList<Error> decoded = original.ToRpcException().Trailers.ToErrors();

        // Assert
        decoded[0].Source.Should().BeNull();
        decoded[0].Target.Should().BeNull();
    }

    // ── Decoding trailers directly ──
    [Fact]
    public void ToErrors_WithNoTrailers_ReturnsEmpty() =>
        new Metadata().ToErrors().Should().BeEmpty();

    [Fact]
    public void ToErrors_WithNullTrailers_ReturnsEmpty()
    {
        // Arrange
        Metadata? trailers = null;

        // Act + Assert
        trailers.ToErrors().Should().BeEmpty();
    }

    [Fact]
    public void ToErrors_StopsAtTheFirstMissingIndex()
    {
        // Arrange: index 0 present, index 1 skipped, index 2 present but unreachable
        var trailers = new Metadata
        {
            { "error-0-code", "First.Code" },
            { "error-0-message", "First message" },
            { "error-0-type", "Validation" },
            { "error-2-code", "Third.Code" },
            { "error-2-message", "Third message" },
            { "error-2-type", "Validation" },
        };

        // Act
        IReadOnlyList<Error> decoded = trailers.ToErrors();

        // Assert
        decoded.Should().ContainSingle("the encoder writes contiguous indices, so a gap ends the sequence");
        decoded[0].Code.Should().Be("First.Code");
    }

    [Fact]
    public void ToErrors_WithMissingMessage_DecodesTheEmptyString()
    {
        // Arrange
        var trailers = new Metadata
        {
            { "error-0-code", "Test.Code" },
            { "error-0-type", "Validation" },
        };

        // Act
        IReadOnlyList<Error> decoded = trailers.ToErrors();

        // Assert
        decoded[0].Message.Should().BeEmpty();
    }

    [Theory]
    [InlineData("notAType")]
    [InlineData("validation")]
    public void ToErrors_WithUnrecognizedType_FallsBackToFailure(string typeText)
    {
        // Arrange: the wire form is the exact enum member name, matched case-sensitively
        var trailers = new Metadata
        {
            { "error-0-code", "Test.Code" },
            { "error-0-message", "Test message" },
            { "error-0-type", typeText },
        };

        // Act
        IReadOnlyList<Error> decoded = trailers.ToErrors();

        // Assert
        decoded[0].Type.Should().Be(
            ErrorType.Failure,
            "a newer peer adding an error type must not break an older client");
    }

    // ── RpcException.ToResult ──
    [Fact]
    public void ToResult_WithStructuredTrailers_ReturnsTheDecodedErrors()
    {
        // Arrange
        IReadOnlyList<Error> original = [Error.NotFoundError("Session.NotFound", "Session not found", source: "Svc")];
        var exception = original.ToRpcException();

        // Act
        var result = exception.ToResult();

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Errors.Should().BeEquivalentTo(original);
    }

    [Fact]
    public void ToResultOfT_WithStructuredTrailers_ReturnsTheDecodedErrors()
    {
        // Arrange
        IReadOnlyList<Error> original = [Error.Forbidden("Session.Forbidden", "Access denied.")];
        var exception = original.ToRpcException();

        // Act
        var result = exception.ToResult<int>();

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Errors.Should().BeEquivalentTo(original);
        result.Value.Should().Be(0);
    }

    [Fact]
    public void ToResult_WithoutTrailers_SynthesizesATransportError()
    {
        // Arrange
        var exception = new RpcException(new Status(StatusCode.Unavailable, "Connection reset"));

        // Act
        var result = exception.ToResult("GetEventLiveInfoAsync");

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle();
        result.Errors[0].Code.Should().Be("Grpc.Unavailable");
        result.Errors[0].Message.Should().Be("Connection reset");
        result.Errors[0].Source.Should().Be("GetEventLiveInfoAsync");
        result.Errors[0].Type.Should().Be(ErrorType.Failure);
    }

    [Fact]
    public void ToResultOfT_WithoutTrailers_SynthesizesATransportError()
    {
        // Arrange
        var exception = new RpcException(new Status(StatusCode.DeadlineExceeded, "Deadline Exceeded"));

        // Act
        var result = exception.ToResult<string>("GetSessionIdsByEventAsync");

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Errors[0].Code.Should().Be("Grpc.DeadlineExceeded");
        result.Errors[0].Source.Should().Be("GetSessionIdsByEventAsync");
    }

    [Fact]
    public void ToResult_WhenSourceOmitted_StampsTheCallingMember()
    {
        // Arrange
        var exception = new RpcException(new Status(StatusCode.Internal, "Boom"));

        // Act
        var result = exception.ToResult();

        // Assert
        result.Errors[0].Source.Should().Be(
            nameof(ToResult_WhenSourceOmitted_StampsTheCallingMember),
            "the source defaults to the caller, which is what the hand-written adapters passed by hand");
    }

    [Fact]
    public void ToResult_WithNullException_ThrowsArgumentNullException()
    {
        // Arrange
        RpcException exception = null!;

        // Act + Assert
        FluentActions.Invoking(() => exception.ToResult("Any"))
            .Should().Throw<ArgumentNullException>();
    }
}

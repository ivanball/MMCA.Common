using AwesomeAssertions;
using Grpc.Core;
using MMCA.Common.Grpc;
using MMCA.Common.Grpc.Exceptions;
using MMCA.Common.Shared.Abstractions;
using Xunit;

namespace MMCA.Common.Grpc.Tests;

/// <summary>
/// Verifies the <see cref="ErrorType"/> → <see cref="StatusCode"/> mapping mirrors
/// <c>ErrorHttpMapping</c> in <c>MMCA.Common.API</c>, and that the helpers correctly
/// surface result failures as RpcExceptions for the gRPC transport.
/// </summary>
public sealed class ResultGrpcExtensionsTests
{
    [Theory]
    [InlineData(ErrorType.Validation, StatusCode.InvalidArgument)]
    [InlineData(ErrorType.Invariant, StatusCode.InvalidArgument)]
    [InlineData(ErrorType.Failure, StatusCode.InvalidArgument)]
    [InlineData(ErrorType.NotFound, StatusCode.NotFound)]
    [InlineData(ErrorType.Conflict, StatusCode.Aborted)]
    [InlineData(ErrorType.Unauthorized, StatusCode.Unauthenticated)]
    [InlineData(ErrorType.Forbidden, StatusCode.PermissionDenied)]
    [InlineData(ErrorType.UnprocessableEntity, StatusCode.FailedPrecondition)]
    public void ErrorType_MapsToExpectedGrpcStatus(ErrorType errorType, StatusCode expected)
    {
        // Act
        var actual = errorType.ToGrpcStatusCode();

        // Assert
        actual.Should().Be(expected);
    }

    [Fact]
    public void ThrowIfFailure_OnSuccess_DoesNotThrow()
    {
        // Arrange
        var success = Result.Success();

        // Act + Assert
        var act = success.ThrowIfFailure;
        act.Should().NotThrow();
    }

    [Fact]
    public void ThrowIfFailure_OnFailure_ThrowsResultFailureException()
    {
        // Arrange
        var error = Error.NotFoundError("Test.NotFound", "Item not found");
        var failure = Result.Failure(error);

        // Act
        var act = failure.ThrowIfFailure;

        // Assert
        var exception = act.Should().Throw<ResultFailureException>().Which;
        exception.Errors.Should().ContainSingle();
        exception.Errors[0].Code.Should().Be("Test.NotFound");
    }

    [Fact]
    public void ToRpcException_PopulatesStatusAndTrailersFromFirstError()
    {
        // Arrange
        IReadOnlyList<Error> errors =
        [
            Error.Conflict("Test.Conflict", "Already exists", source: "TestService", target: "Name"),
            Error.Validation("Test.Validation", "Bad input"),
        ];

        // Act
        var exception = errors.ToRpcException();

        // Assert: status code derives from the FIRST error's type
        exception.StatusCode.Should().Be(StatusCode.Aborted);
        exception.Status.Detail.Should().Contain("Test.Conflict").And.Contain("Test.Validation");

        // Trailers carry every error's structured fields
        exception.Trailers.GetValue("error-0-code").Should().Be("Test.Conflict");
        exception.Trailers.GetValue("error-0-message").Should().Be("Already exists");
        exception.Trailers.GetValue("error-0-type").Should().Be(nameof(ErrorType.Conflict));
        exception.Trailers.GetValue("error-0-source").Should().Be("TestService");
        exception.Trailers.GetValue("error-0-target").Should().Be("Name");

        exception.Trailers.GetValue("error-1-code").Should().Be("Test.Validation");
        exception.Trailers.GetValue("error-1-type").Should().Be(nameof(ErrorType.Validation));
    }

    [Fact]
    public void ToRpcException_OnEmptyErrorList_UsesInternalStatus()
    {
        // Arrange
        IReadOnlyList<Error> errors = [];

        // Act
        var exception = errors.ToRpcException();

        // Assert
        exception.StatusCode.Should().Be(StatusCode.Internal);
        exception.Status.Detail.Should().Be("Unspecified failure");
    }

    [Fact]
    public void UnwrapOrThrow_OnSuccess_ReturnsValue()
    {
        // Arrange
        var success = Result.Success(42);

        // Act
        var value = success.UnwrapOrThrow();

        // Assert
        value.Should().Be(42);
    }

    [Fact]
    public void UnwrapOrThrow_OnFailure_ThrowsResultFailureException()
    {
        // Arrange
        var failure = Result.Failure<int>([Error.NotFoundError("Missing", "no value")]);

        // Act
        var act = failure.UnwrapOrThrow;

        // Assert
        act.Should().Throw<ResultFailureException>()
            .Which.Errors.Should().ContainSingle(e => e.Code == "Missing");
    }
}

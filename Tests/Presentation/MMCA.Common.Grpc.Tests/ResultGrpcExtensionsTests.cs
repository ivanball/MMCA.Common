using AwesomeAssertions;
using Grpc.Core;
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
    [InlineData(ErrorType.Unexpected, StatusCode.Internal)]
    [InlineData(ErrorType.TooManyRequests, StatusCode.ResourceExhausted)]
    public void ErrorType_MapsToExpectedGrpcStatus(ErrorType errorType, StatusCode expected)
    {
        // Act
        var actual = errorType.ToGrpcStatusCode();

        // Assert
        actual.Should().Be(expected);
    }

    public static TheoryData<ErrorType> AllErrorTypes => [.. Enum.GetValues<ErrorType>()];

    /// <summary>
    /// Exhaustiveness pin: the map is private and its fallback (<see cref="StatusCode.InvalidArgument"/>)
    /// is also a legitimate explicit answer, so "explicit" is asserted as: every value maps somewhere
    /// other than the fallback, except the three that deliberately map there (Validation, Invariant,
    /// Failure, mirroring the 400 they get over HTTP). A new ErrorType left unmapped fails here.
    /// </summary>
    /// <param name="errorType">Each value of the enum.</param>
    [Theory]
    [MemberData(nameof(AllErrorTypes))]
    public void EveryErrorType_MapsExplicitly_NotThroughTheFallback(ErrorType errorType)
    {
        ErrorType[] legitimatelyInvalidArgument = [ErrorType.Validation, ErrorType.Invariant, ErrorType.Failure];

        var actual = errorType.ToGrpcStatusCode();

        if (legitimatelyInvalidArgument.Contains(errorType))
        {
            actual.Should().Be(StatusCode.InvalidArgument);
        }
        else
        {
            actual.Should().NotBe(StatusCode.InvalidArgument, "only an unmapped ErrorType reaches the fallback");
        }
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

    // L65: gRPC text metadata is printable ASCII only. A message with an accented letter and a
    // newline must reach the wire escaped, and still decode back to the original text.
    [Fact]
    public void ToRpcException_WithANonAsciiMultiLineMessage_WritesPrintableAsciiTrailersThatRoundTrip()
    {
        const string message = "Caf\u00e9 not found\nsecond line";
        IReadOnlyList<Error> errors =
        [
            Error.NotFoundError("Test.NotFound", message, source: "Svc\u00e9", target: "Na\nme"),
        ];

        var exception = errors.ToRpcException();

        exception.Trailers.Should().OnlyContain(entry => entry.Value.All(c => c >= ' ' && c <= '~'));
        var decoded = exception.Trailers.ToErrors();
        decoded[0].Message.Should().Be(message);
        decoded[0].Source.Should().Be("Svc\u00e9");
        decoded[0].Target.Should().Be("Na\nme");
    }

    [Fact]
    public void ToRpcException_PopulatesStatusAndTrailersFromMostSevereError()
    {
        // Arrange
        IReadOnlyList<Error> errors =
        [
            Error.Conflict("Test.Conflict", "Already exists", source: "TestService", target: "Name"),
            Error.Validation("Test.Validation", "Bad input"),
        ];

        // Act
        var exception = errors.ToRpcException();

        // Assert: status code derives from the MOST SEVERE error's type, never from position
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
    public void ToRpcException_OnUnexpectedError_UsesInternalStatus()
    {
        // Arrange
        IReadOnlyList<Error> errors = [Error.Unexpected("Test.Unexpected", "The server broke")];

        // Act
        var exception = errors.ToRpcException();

        // Assert
        exception.StatusCode.Should().Be(StatusCode.Internal);
        exception.Trailers.GetValue("error-0-type").Should().Be(nameof(ErrorType.Unexpected));
    }

    [Fact]
    public void ToRpcException_UnauthorizedBehindValidation_IsNotDowngradedToInvalidArgument()
    {
        // Arrange: the exact ordering an aggregate from Result.Combine produces, where a
        // positional pick would answer InvalidArgument and hide the authentication failure.
        var combined = Result.Combine(
            Result.Failure(Error.Validation("Test.Validation", "Bad input")),
            Result.Failure(Error.Unauthorized("Test.Unauthorized", "Not authenticated")));

        // Act
        var exception = combined.Errors.ToRpcException();

        // Assert
        exception.StatusCode.Should().Be(StatusCode.Unauthenticated);
    }

    [Fact]
    public void ToRpcException_RankedStatus_StillCarriesEveryErrorInTheTrailers()
    {
        // Arrange
        IReadOnlyList<Error> errors =
        [
            Error.Validation("Test.Validation", "Bad input"),
            Error.Unexpected("Test.Unexpected", "The server broke"),
        ];

        // Act
        var exception = errors.ToRpcException();

        // Assert: ranking picks the status only; the payload keeps every error.
        exception.StatusCode.Should().Be(StatusCode.Internal);
        exception.Trailers.GetValue("error-0-code").Should().Be("Test.Validation");
        exception.Trailers.GetValue("error-1-code").Should().Be("Test.Unexpected");
        exception.Status.Detail.Should().Contain("Test.Validation").And.Contain("Test.Unexpected");
    }

    [Fact]
    public void ToRpcException_WithEqualRankErrors_KeepsTheEarliestError()
    {
        // Arrange
        IReadOnlyList<Error> errors =
        [
            Error.Validation("Test.Validation", "Bad input"),
            Error.Invariant("Test.Invariant", "Rule broken"),
        ];

        // Act
        var exception = errors.ToRpcException();

        // Assert
        exception.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    [Fact]
    public void ToRpcException_ClassifiesTheSameAggregate_AsTheHttpEdgeDoes()
    {
        // Arrange: the shared ranking is what makes the two edges agree. Assert the gRPC status is
        // the one belonging to the error type the ranking selects, for several orderings of the
        // same aggregate.
        Error[] pool =
        [
            Error.Validation("Test.Validation", "Bad input"),
            Error.NotFoundError("Test.NotFound", "Missing"),
            Error.Forbidden("Test.Forbidden", "Denied"),
        ];

        IReadOnlyList<Error>[] orderings =
        [
            [pool[0], pool[1], pool[2]],
            [pool[2], pool[0], pool[1]],
            [pool[1], pool[2], pool[0]],
        ];

        foreach (var ordering in orderings)
        {
            // Act
            var exception = ordering.ToRpcException();

            // Assert
            var expected = ErrorTypeSeverity.MostSevere(ordering).Type.ToGrpcStatusCode();
            exception.StatusCode.Should().Be(expected).And.Be(StatusCode.PermissionDenied);
        }
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

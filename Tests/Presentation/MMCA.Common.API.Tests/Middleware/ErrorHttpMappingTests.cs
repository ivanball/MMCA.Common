using AwesomeAssertions;
using MMCA.Common.API.Middleware;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.API.Tests.Middleware;

/// <summary>
/// Exhaustiveness pin for the hand-kept <see cref="ErrorType"/> to HTTP status map: every enum value
/// must have an explicit entry, so a newly added <see cref="ErrorType"/> fails here instead of
/// silently answering with the 400 fallback of <c>GetStatusCode</c>.
/// </summary>
public sealed class ErrorHttpMappingTests
{
    public static TheoryData<ErrorType> AllErrorTypes => [.. Enum.GetValues<ErrorType>()];

    [Theory]
    [MemberData(nameof(AllErrorTypes))]
    public void EveryErrorType_HasAnExplicitHttpStatusEntry(ErrorType errorType) =>
        ErrorHttpMapping.ErrorTypeToStatusCode.Should().ContainKey(
            errorType,
            "an unmapped ErrorType would fall back to 400 without anyone choosing that status");

    // The one owner of the ErrorType-to-status matrix: ApiControllerBase and
    // UnhandledResultFailureFilter both delegate here and keep only a smoke case each.
    // A lockout (too many failed attempts) is a throttle, not a failed authentication, hence 429.
    [Theory]
    [InlineData(ErrorType.Validation, 400)]
    [InlineData(ErrorType.Invariant, 400)]
    [InlineData(ErrorType.NotFound, 404)]
    [InlineData(ErrorType.Conflict, 409)]
    [InlineData(ErrorType.Unauthorized, 401)]
    [InlineData(ErrorType.Forbidden, 403)]
    [InlineData(ErrorType.UnprocessableEntity, 422)]
    [InlineData(ErrorType.Failure, 400)]
    [InlineData(ErrorType.Unexpected, 500)]
    [InlineData(ErrorType.TooManyRequests, 429)]
    public void GetStatusCode_MapsEachErrorTypeToItsHttpStatus(ErrorType errorType, int expectedStatus) =>
        ErrorHttpMapping.GetStatusCode(errorType).Should().Be(expectedStatus);
}

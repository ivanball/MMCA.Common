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

    // A lockout (too many failed attempts) is a throttle, not a failed authentication.
    [Fact]
    public void TooManyRequests_MapsTo429() =>
        ErrorHttpMapping.GetStatusCode(ErrorType.TooManyRequests).Should().Be(429);
}

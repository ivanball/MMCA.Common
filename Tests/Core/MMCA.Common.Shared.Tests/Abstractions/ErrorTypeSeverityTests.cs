using AwesomeAssertions;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Shared.Tests.Abstractions;

/// <summary>
/// The severity ranking is the one classification rule every transport edge shares, so it is
/// tested here rather than once per edge.
/// </summary>
public sealed class ErrorTypeSeverityTests
{
    [Theory]
    [InlineData(ErrorType.Unexpected, ErrorType.Unauthorized)]
    [InlineData(ErrorType.Unauthorized, ErrorType.Forbidden)]
    [InlineData(ErrorType.Forbidden, ErrorType.Conflict)]
    [InlineData(ErrorType.Conflict, ErrorType.NotFound)]
    [InlineData(ErrorType.NotFound, ErrorType.UnprocessableEntity)]
    [InlineData(ErrorType.UnprocessableEntity, ErrorType.Validation)]
    [InlineData(ErrorType.UnprocessableEntity, ErrorType.Invariant)]
    [InlineData(ErrorType.UnprocessableEntity, ErrorType.Failure)]
    public void Rank_FollowsTheDocumentedOrder(ErrorType moreSevere, ErrorType lessSevere) =>
        ErrorTypeSeverity.Rank(moreSevere).Should().BeGreaterThan(ErrorTypeSeverity.Rank(lessSevere));

    [Fact]
    public void Rank_TreatsThe400Family_AsEqualSeverity()
    {
        var validation = ErrorTypeSeverity.Rank(ErrorType.Validation);

        ErrorTypeSeverity.Rank(ErrorType.Invariant).Should().Be(validation);
        ErrorTypeSeverity.Rank(ErrorType.Failure).Should().Be(validation);
    }

    [Fact]
    public void Rank_ForAnUnmappedErrorType_IsLowest() =>
        ErrorTypeSeverity.Rank((ErrorType)999).Should().Be(0);

    [Fact]
    public void Rank_CoversEveryDeclaredErrorType()
    {
        foreach (ErrorType errorType in Enum.GetValues<ErrorType>())
        {
            ErrorTypeSeverity.Rank(errorType).Should().BeGreaterThan(
                0,
                "every declared ErrorType needs a rank or it would silently sort below a validation error");
        }
    }

    [Fact]
    public void MostSevere_WithASingleError_ReturnsIt()
    {
        var error = Error.Validation("Test.Validation", "Validation failed");

        ErrorTypeSeverity.MostSevere([error]).Should().BeSameAs(error);
    }

    [Fact]
    public void MostSevere_IgnoresPosition()
    {
        var forbidden = Error.Forbidden("Test.Forbidden", "Access denied");
        IReadOnlyList<Error> errors = [Error.Validation("Test.Validation", "Validation failed"), forbidden];

        ErrorTypeSeverity.MostSevere(errors).Should().BeSameAs(forbidden);
    }

    [Fact]
    public void MostSevere_UnauthorizedOutranksForbidden()
    {
        var unauthorized = Error.Unauthorized("Test.Unauthorized", "Not authenticated");
        IReadOnlyList<Error> errors = [Error.Forbidden("Test.Forbidden", "Access denied"), unauthorized];

        ErrorTypeSeverity.MostSevere(errors).Should().BeSameAs(unauthorized);
    }

    [Fact]
    public void MostSevere_WithEqualRanks_KeepsTheEarliestError()
    {
        var first = Error.Validation("Test.Validation", "Validation failed");
        IReadOnlyList<Error> errors =
        [
            first,
            Error.Invariant("Test.Invariant", "Invariant violated"),
            Error.Failure("Test.Failure", "General failure"),
        ];

        ErrorTypeSeverity.MostSevere(errors).Should().BeSameAs(first);
    }

    [Fact]
    public void MostSevere_OverACombinedResult_PicksTheSevereBranch()
    {
        var combined = Result.Combine(
            Result.Failure(Error.NotFoundError("Test.NotFound", "Entity not found")),
            Result.Failure(Error.Unexpected("Test.Unexpected", "The server broke")),
            Result.Failure(Error.Validation("Test.Validation", "Validation failed")));

        ErrorTypeSeverity.MostSevere(combined.Errors).Type.Should().Be(ErrorType.Unexpected);
    }

    [Fact]
    public void MostSevere_WithNoErrors_Throws()
    {
        var act = () => ErrorTypeSeverity.MostSevere([]);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void MostSevere_WithNull_Throws()
    {
        var act = () => ErrorTypeSeverity.MostSevere(null!);

        act.Should().Throw<ArgumentNullException>();
    }
}

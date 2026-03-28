using AwesomeAssertions;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Shared.Tests.Abstractions;

public class ResultTests
{
    // ── Success ──
    [Fact]
    public void Success_ReturnsSuccessResult()
    {
        var result = Result.Success();

        result.IsSuccess.Should().BeTrue();
        result.IsFailure.Should().BeFalse();
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public void Success_WithValue_ReturnsSuccessResultWithValue()
    {
        var result = Result.Success(42);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(42);
    }

    // ── Failure ──
    [Fact]
    public void Failure_WithError_ReturnsFailureResult()
    {
        var error = Error.Validation("test", "test error");

        var result = Result.Failure(error);

        result.IsFailure.Should().BeTrue();
        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Code.Should().Be("test");
    }

    [Fact]
    public void Failure_WithMultipleErrors_ReturnsAllErrors()
    {
        var errors = new[]
        {
            Error.Validation("err1", "first"),
            Error.Validation("err2", "second"),
        };

        var result = Result.Failure(errors);

        result.Errors.Should().HaveCount(2);
    }

    [Fact]
    public void Failure_Generic_ReturnsFailureWithNullValue()
    {
        var result = Result.Failure<int>(Error.Validation("test", "test"));

        result.IsFailure.Should().BeTrue();
        result.Value.Should().Be(default);
    }

    // ── Combine ──
    [Fact]
    public void Combine_AllSuccess_ReturnsSuccess()
    {
        var result = Result.Combine(
            Result.Success(),
            Result.Success());

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void Combine_WithFailures_AggregatesAllErrors()
    {
        var result = Result.Combine(
            Result.Failure(Error.Validation("err1", "first")),
            Result.Success(),
            Result.Failure(Error.Validation("err2", "second")));

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().HaveCount(2);
        result.Errors.Should().Contain(e => e.Code == "err1");
        result.Errors.Should().Contain(e => e.Code == "err2");
    }

    // ── Match ──
    [Fact]
    public void Match_OnSuccess_CallsOnSuccessFunc()
    {
        var result = Result.Success(42);

        var output = result.Match(
            onSuccess: v => v * 2,
            onFailure: _ => -1);

        output.Should().Be(84);
    }

    [Fact]
    public void Match_OnFailure_CallsOnFailureFunc()
    {
        var result = Result.Failure<int>(Error.Validation("test", "msg"));

        var output = result.Match(
            onSuccess: v => v * 2,
            onFailure: errors => errors.Count());

        output.Should().Be(1);
    }
}

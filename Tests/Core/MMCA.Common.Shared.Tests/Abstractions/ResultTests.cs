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

    // ── Failure guards: an empty error collection must not produce a "failure" that IsSuccess ──
    [Fact]
    public void Failure_EmptyErrors_ThrowsArgumentException()
    {
        var act = () => Result.Failure(Enumerable.Empty<Error>());

        act.Should().Throw<ArgumentException>()
            .WithMessage("*at least one error*");
    }

    [Fact]
    public void Failure_Generic_EmptyErrors_ThrowsArgumentException()
    {
        var act = () => Result.Failure<int>(Enumerable.Empty<Error>());

        act.Should().Throw<ArgumentException>()
            .WithMessage("*at least one error*");
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

    [Fact]
    public void Combine_NoArguments_ThrowsArgumentException()
    {
        var act = () => Result.Combine();

        act.Should().Throw<ArgumentException>()
            .WithMessage("*At least one result*");
    }

    // ── Map ──
    [Fact]
    public void Map_OnSuccess_TransformsValue()
    {
        var result = Result.Success(21).Map(v => v * 2);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(42);
    }

    [Fact]
    public void Map_OnFailure_PropagatesErrorsWithoutInvokingMapper()
    {
        var mapperInvoked = false;
        var failure = Result.Failure<int>(Error.Validation("err1", "first"));

        var result = failure.Map(v =>
        {
            mapperInvoked = true;
            return v * 2;
        });

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle().Which.Code.Should().Be("err1");
        mapperInvoked.Should().BeFalse("a failed result must never run the mapping function");
    }

    // ── BindAsync ──
    [Fact]
    public async Task BindAsync_OnSuccess_ChainsToBoundOperation()
    {
        var result = await Result.Success(21)
            .BindAsync(v => Task.FromResult(Result.Success(v * 2)));

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(42);
    }

    [Fact]
    public async Task BindAsync_OnSuccess_PropagatesBoundFailure()
    {
        var result = await Result.Success(21)
            .BindAsync(_ => Task.FromResult(Result.Failure<int>(Error.Validation("bound", "bound failed"))));

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle().Which.Code.Should().Be("bound");
    }

    [Fact]
    public async Task BindAsync_OnFailure_ShortCircuitsWithoutInvokingBinder()
    {
        var binderInvoked = false;
        var failure = Result.Failure<int>(Error.Validation("err1", "first"));

        var result = await failure.BindAsync(v =>
        {
            binderInvoked = true;
            return Task.FromResult(Result.Success(v * 2));
        });

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle().Which.Code.Should().Be("err1");
        binderInvoked.Should().BeFalse("a failed result must short-circuit the bound operation");
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

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

    // ── Implicit conversions ──
    [Fact]
    public void ImplicitConversion_FromError_ProducesFailedResult()
    {
        Result result = Error.Forbidden("Access.Denied", "not allowed");

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle().Which.Type.Should().Be(ErrorType.Forbidden);
    }

    [Fact]
    public void ImplicitConversion_FromError_ProducesFailedTypedResult()
    {
        Result<int> result = Error.NotFoundError("Order.NotFound", "no such order");

        result.IsFailure.Should().BeTrue();
        result.Value.Should().Be(default);
        result.Errors.Should().ContainSingle().Which.Code.Should().Be("Order.NotFound");
    }

    [Fact]
    public void ImplicitConversion_FromValue_ProducesSuccessfulTypedResult()
    {
        Result<int> result = 42;

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(42);
    }

    [Fact]
    public void ImplicitConversion_FromNullValue_ProducesSuccessfulTypedResult()
    {
        Result<string?> result = (string?)null;

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeNull();
    }

    [Fact]
    public void FromError_WithNull_ThrowsArgumentNullException()
    {
        var act = () => Result.FromError(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    // ── Result.Match / OnFailure / Bind (non-generic) ──
    [Fact]
    public void NonGenericMatch_OnSuccess_CallsOnSuccessFunc()
    {
        var output = Result.Success().Match(
            onSuccess: () => "ok",
            onFailure: _ => "bad");

        output.Should().Be("ok");
    }

    [Fact]
    public void NonGenericMatch_OnFailure_CallsOnFailureFunc()
    {
        var output = Result.Failure(Error.Conflict("dup", "duplicate")).Match(
            onSuccess: () => "ok",
            onFailure: errors => errors.First().Code);

        output.Should().Be("dup");
    }

    [Fact]
    public void OnFailure_OnFailure_RunsActionAndReturnsSameInstance()
    {
        var failure = Result.Failure(Error.Validation("err1", "first"));
        IReadOnlyList<Error>? seen = null;

        var returned = failure.OnFailure(errors => seen = errors);

        returned.Should().BeSameAs(failure);
        seen.Should().ContainSingle().Which.Code.Should().Be("err1");
    }

    [Fact]
    public void OnFailure_OnSuccess_DoesNotRunAction()
    {
        var invoked = false;
        var success = Result.Success();

        var returned = success.OnFailure(_ => invoked = true);

        returned.Should().BeSameAs(success);
        invoked.Should().BeFalse("a successful result must never run the failure side effect");
    }

    [Fact]
    public void NonGenericBind_OnSuccess_ChainsToBoundOperation()
    {
        var result = Result.Success().Bind(() => Result.Failure(Error.Conflict("bound", "bound failed")));

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle().Which.Code.Should().Be("bound");
    }

    [Fact]
    public void NonGenericBind_OnFailure_ShortCircuitsWithoutInvokingBinder()
    {
        var binderInvoked = false;
        var failure = Result.Failure(Error.Validation("err1", "first"));

        var result = failure.Bind(() =>
        {
            binderInvoked = true;
            return Result.Success();
        });

        result.Should().BeSameAs(failure);
        binderInvoked.Should().BeFalse("a failed result must short-circuit the bound operation");
    }

    // ── Bind (generic, sync) ──
    [Fact]
    public void Bind_OnSuccess_ChainsToBoundOperation()
    {
        var result = Result.Success(21).Bind(v => Result.Success(v * 2));

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(42);
    }

    [Fact]
    public void Bind_OnFailure_ShortCircuitsWithoutInvokingBinder()
    {
        var binderInvoked = false;
        var failure = Result.Failure<int>(Error.Validation("err1", "first"));

        var result = failure.Bind(v =>
        {
            binderInvoked = true;
            return Result.Success(v * 2);
        });

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle().Which.Code.Should().Be("err1");
        binderInvoked.Should().BeFalse();
    }

    // ── Tap ──
    [Fact]
    public void Tap_OnSuccess_RunsActionAndReturnsSameInstance()
    {
        var success = Result.Success(42);
        var seen = 0;

        var returned = success.Tap(v => seen = v);

        returned.Should().BeSameAs(success);
        seen.Should().Be(42);
    }

    [Fact]
    public void Tap_OnFailure_DoesNotRunAction()
    {
        var invoked = false;
        var failure = Result.Failure<int>(Error.Validation("err1", "first"));

        var returned = failure.Tap(_ => invoked = true);

        returned.Should().BeSameAs(failure);
        invoked.Should().BeFalse("a failed result carries no value to tap");
    }

    // ── Ensure ──
    [Fact]
    public void Ensure_OnSuccess_PredicateHolds_ReturnsSameInstance()
    {
        var success = Result.Success(42);

        var returned = success.Ensure(v => v > 0, Error.Validation("neg", "must be positive"));

        returned.Should().BeSameAs(success);
    }

    [Fact]
    public void Ensure_OnSuccess_PredicateFails_ReturnsSuppliedError()
    {
        var result = Result.Success(-1).Ensure(v => v > 0, Error.Validation("neg", "must be positive"));

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle().Which.Code.Should().Be("neg");
    }

    [Fact]
    public void Ensure_OnFailure_ShortCircuitsWithoutInvokingPredicate()
    {
        var predicateInvoked = false;
        var failure = Result.Failure<int>(Error.NotFoundError("gone", "missing"));

        var result = failure.Ensure(
            v =>
            {
                predicateInvoked = true;
                return v > 0;
            },
            Error.Validation("neg", "must be positive"));

        result.Should().BeSameAs(failure);
        predicateInvoked.Should().BeFalse("a failed result carries no value to test");
    }

    // ── MatchAsync ──
    [Fact]
    public async Task MatchAsync_OnSuccess_AwaitsSuccessBranch()
    {
        var output = await Result.Success(21).MatchAsync(
            onSuccess: v => Task.FromResult(v * 2),
            onFailure: _ => Task.FromResult(-1));

        output.Should().Be(42);
    }

    [Fact]
    public async Task MatchAsync_OnFailure_AwaitsFailureBranch()
    {
        var successInvoked = false;

        var output = await Result.Failure<int>(Error.Validation("err1", "first")).MatchAsync(
            onSuccess: v =>
            {
                successInvoked = true;
                return Task.FromResult(v * 2);
            },
            onFailure: errors => Task.FromResult(errors.Count()));

        output.Should().Be(1);
        successInvoked.Should().BeFalse();
    }
}

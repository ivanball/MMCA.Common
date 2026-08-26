using AwesomeAssertions;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Shared.Tests.Abstractions;

/// <summary>
/// Covers the Task-returning combinators in <see cref="ResultExtensions"/>: an asynchronous
/// chain must short-circuit on failure exactly like the synchronous instance combinators.
/// </summary>
public class ResultExtensionsTests
{
    private static Task<Result<int>> SuccessTask(int value) => Task.FromResult(Result.Success(value));

    private static Task<Result<int>> FailureTask(string code) =>
        Task.FromResult(Result.Failure<int>(Error.Validation(code, "failed")));

    // ── BindAsync (async binder) ──
    [Fact]
    public async Task BindAsync_OnSuccess_ChainsToBoundOperation()
    {
        var result = await SuccessTask(21).BindAsync(v => Task.FromResult(Result.Success(v * 2)));

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(42);
    }

    [Fact]
    public async Task BindAsync_OnFailure_ShortCircuitsWithoutInvokingBinder()
    {
        var binderInvoked = false;

        var result = await FailureTask("err1").BindAsync(v =>
        {
            binderInvoked = true;
            return Task.FromResult(Result.Success(v * 2));
        });

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle().Which.Code.Should().Be("err1");
        binderInvoked.Should().BeFalse();
    }

    // ── BindAsync (sync binder) ──
    [Fact]
    public async Task BindAsync_WithSyncBinder_OnSuccess_ChainsToBoundOperation()
    {
        var result = await SuccessTask(21).BindAsync(v => Result.Success(v * 2));

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(42);
    }

    [Fact]
    public async Task BindAsync_WithSyncBinder_OnFailure_ShortCircuitsWithoutInvokingBinder()
    {
        var binderInvoked = false;

        var result = await FailureTask("err1").BindAsync(v =>
        {
            binderInvoked = true;
            return Result.Success(v * 2);
        });

        result.IsFailure.Should().BeTrue();
        binderInvoked.Should().BeFalse();
    }

    // ── MapAsync ──
    [Fact]
    public async Task MapAsync_OnSuccess_TransformsValue()
    {
        var result = await SuccessTask(21).MapAsync(v => v * 2);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(42);
    }

    [Fact]
    public async Task MapAsync_OnFailure_PropagatesErrorsWithoutInvokingMapper()
    {
        var mapperInvoked = false;

        var result = await FailureTask("err1").MapAsync(v =>
        {
            mapperInvoked = true;
            return v * 2;
        });

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle().Which.Code.Should().Be("err1");
        mapperInvoked.Should().BeFalse();
    }

    // ── TapAsync ──
    [Fact]
    public async Task TapAsync_OnSuccess_RunsActionAndReturnsResult()
    {
        var seen = 0;

        var result = await SuccessTask(42).TapAsync(v =>
        {
            seen = v;
            return Task.CompletedTask;
        });

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(42);
        seen.Should().Be(42);
    }

    [Fact]
    public async Task TapAsync_OnFailure_DoesNotRunAction()
    {
        var invoked = false;

        var result = await FailureTask("err1").TapAsync(_ =>
        {
            invoked = true;
            return Task.CompletedTask;
        });

        result.IsFailure.Should().BeTrue();
        invoked.Should().BeFalse();
    }

    // ── MatchAsync ──
    [Fact]
    public async Task MatchAsync_OnSuccess_CallsOnSuccessFunc()
    {
        var output = await SuccessTask(21).MatchAsync(
            onSuccess: v => v * 2,
            onFailure: _ => -1);

        output.Should().Be(42);
    }

    [Fact]
    public async Task MatchAsync_OnFailure_CallsOnFailureFunc()
    {
        var output = await FailureTask("err1").MatchAsync(
            onSuccess: v => v * 2,
            onFailure: errors => errors.Count());

        output.Should().Be(1);
    }

    // ── Composition: the whole point of the Task overloads ──
    [Fact]
    public async Task Chain_ComposesWithoutIntermediateAwaits()
    {
        var output = await SuccessTask(5)
            .MapAsync(v => v + 1)
            .BindAsync(v => Task.FromResult(Result.Success(v * 10)))
            .MatchAsync(
                onSuccess: v => v.ToString(System.Globalization.CultureInfo.InvariantCulture),
                onFailure: _ => "failed");

        output.Should().Be("60");
    }

    [Fact]
    public async Task Chain_ShortCircuitsAtFirstFailure()
    {
        var laterStepInvoked = false;

        var output = await SuccessTask(5)
            .BindAsync(_ => Result.Failure<int>(Error.Conflict("stop", "conflict")))
            .MapAsync(v =>
            {
                laterStepInvoked = true;
                return v * 10;
            })
            .MatchAsync(
                onSuccess: _ => "ok",
                onFailure: errors => errors.First().Code);

        output.Should().Be("stop");
        laterStepInvoked.Should().BeFalse();
    }
}

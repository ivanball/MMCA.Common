using AwesomeAssertions;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Application.Tests.Decorators;

/// <summary>
/// The timeout decorator contract shared by the command and the query pipeline: no budget keeps the
/// caller's token, an unexhausted budget is invisible, an expired budget is a Request.TimedOut
/// failure, caller cancellation stays an exception, and a non-Result handler still resolves. Each
/// subclass supplies the decorator under test and its two request types.
/// </summary>
/// <typeparam name="TResult">The result type the subclass's handlers return.</typeparam>
public abstract class TimeoutDecoratorTestsBase<TResult>
    where TResult : Result
{
    /// <summary>The type name of the budgeted request, reported as the timed-out error's source.</summary>
    protected abstract string BudgetedRequestName { get; }

    /// <summary>The successful result the inner handler returns.</summary>
    protected abstract TResult SuccessResult { get; }

    /// <summary>
    /// Builds the decorator over an inner handler that answers through <paramref name="inner"/>, and
    /// returns a call that sends it one request: the budgeted request carrying
    /// <paramref name="timeout"/>, or the unbudgeted request when <paramref name="timeout"/> is null.
    /// </summary>
    /// <typeparam name="T">The handler's result type (a non-Result type exercises the lazy factory).</typeparam>
    protected abstract Func<CancellationToken, Task<T>> CreateSut<T>(
        TimeSpan? timeout,
        Func<CancellationToken, Task<T>> inner);

    /// <summary>Extra checks on a successful pass-through (the query also checks the value).</summary>
    protected virtual void AssertPassedThrough(TResult result) => result.IsSuccess.Should().BeTrue();

    // ── A request without a budget keeps the caller's token untouched ──
    [Fact]
    public async Task HandleAsync_RequestWithoutTimeout_DelegatesWithCallerToken()
    {
        using var cts = new CancellationTokenSource();
        CancellationToken token = cts.Token;
        var received = new List<CancellationToken>();

        var handle = CreateSut<TResult>(
            timeout: null,
            ct =>
            {
                received.Add(ct);
                return Task.FromResult(SuccessResult);
            });

        var result = await handle(token);

        result.IsSuccess.Should().BeTrue();
        received.Should().ContainSingle().Which.Should().Be(token);
    }

    // ── A budget that is never exhausted is invisible ──
    [Fact]
    public async Task HandleAsync_WhenHandlerCompletesInsideBudget_ReturnsHandlerResult()
    {
        var handle = CreateSut<TResult>(TimeSpan.FromSeconds(30), _ => Task.FromResult(SuccessResult));

        var result = await handle(CancellationToken.None);

        AssertPassedThrough(result);
    }

    // ── An expired budget becomes a failure result, not an exception ──
    [Fact]
    public async Task HandleAsync_WhenBudgetExpires_ReturnsTimedOutFailure()
    {
        var handle = CreateSut<TResult>(
            TimeSpan.FromMilliseconds(30),
            async ct =>
            {
                await Task.Delay(TimeSpan.FromMinutes(1), ct);
                return SuccessResult;
            });

        var result = await handle(CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        var error = result.Errors.Should().ContainSingle().Subject;
        error.Code.Should().Be("Request.TimedOut");
        error.Source.Should().Be(BudgetedRequestName);
    }

    // ── Cancellation raised by the CALLER stays an exception ──
    // Turning it into a failure result would report an aborted request as a server-side timeout and
    // hide the abort from every caller of the pipeline.
    [Fact]
    public async Task HandleAsync_WhenCallerCancels_RethrowsInsteadOfReturningFailure()
    {
        using var cts = new CancellationTokenSource();

        var handle = CreateSut<TResult>(
            TimeSpan.FromMinutes(5),
            async ct =>
            {
                await cts.CancelAsync();
                await Task.Delay(TimeSpan.FromMinutes(1), ct);
                return SuccessResult;
            });

        Func<Task> act = () => handle(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ── A non-positive budget is treated as "no budget" ──
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task HandleAsync_WhenBudgetIsNotPositive_PassesThroughWithCallerToken(int seconds)
    {
        using var cts = new CancellationTokenSource();
        CancellationToken token = cts.Token;
        var received = new List<CancellationToken>();

        var handle = CreateSut<TResult>(
            TimeSpan.FromSeconds(seconds),
            ct =>
            {
                received.Add(ct);
                return Task.FromResult(SuccessResult);
            });

        var result = await handle(token);

        result.IsSuccess.Should().BeTrue();
        received.Should().ContainSingle().Which.Should().Be(token);
    }

    // Scrutor decorates handlers whose TResult is neither Result nor Result<T> too; the decorator
    // must resolve and pass through rather than fail at type-initialization time.
    [Fact]
    public async Task HandleAsync_NonResultTResult_UnbudgetedRequest_PassesThrough()
    {
        var handle = CreateSut<string>(timeout: null, _ => Task.FromResult("handled"));

        var result = await handle(CancellationToken.None);

        result.Should().Be("handled");
    }
}

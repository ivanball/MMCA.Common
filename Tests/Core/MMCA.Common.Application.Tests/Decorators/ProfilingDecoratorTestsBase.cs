using AwesomeAssertions;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Application.Tests.Decorators;

/// <summary>
/// The profiling decorator contract shared by the command and the query pipeline: delegate once,
/// pass a success or a failure through, and hand the caller's token to the inner handler. Each
/// subclass supplies the decorator under test and its request type.
/// </summary>
/// <typeparam name="TResult">The result type the subclass's handler returns.</typeparam>
public abstract class ProfilingDecoratorTestsBase<TResult>
    where TResult : Result
{
    /// <summary>The successful result the inner handler returns.</summary>
    protected abstract TResult SuccessResult { get; }

    /// <summary>The failed result the inner handler returns.</summary>
    protected abstract TResult FailureResult { get; }

    /// <summary>
    /// Builds the decorator over an inner handler that answers through <paramref name="inner"/>, and
    /// returns a call that sends it one request with the given token.
    /// </summary>
    protected abstract Func<CancellationToken, Task<TResult>> CreateSut(Func<CancellationToken, Task<TResult>> inner);

    /// <summary>Extra checks on a successful pass-through (the query also checks the value).</summary>
    protected virtual void AssertPassedThrough(TResult result) => result.IsSuccess.Should().BeTrue();

    [Fact]
    public async Task HandleAsync_DelegatesCallToInnerHandler()
    {
        var calls = 0;
        var handle = CreateSut(_ =>
        {
            calls++;
            return Task.FromResult(SuccessResult);
        });

        var result = await handle(CancellationToken.None);

        AssertPassedThrough(result);
        calls.Should().Be(1);
    }

    [Fact]
    public async Task HandleAsync_WhenInnerFails_ReturnsFailure()
    {
        var handle = CreateSut(_ => Task.FromResult(FailureResult));

        var result = await handle(CancellationToken.None);

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task HandleAsync_PassesCancellationTokenToInner()
    {
        using var cts = new CancellationTokenSource();
        var token = cts.Token;
        var received = new List<CancellationToken>();
        var handle = CreateSut(ct =>
        {
            received.Add(ct);
            return Task.FromResult(SuccessResult);
        });

        await handle(token);

        received.Should().ContainSingle().Which.Should().Be(token);
    }
}

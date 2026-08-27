using System.Text.Json;
using AwesomeAssertions;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.UI.Services;

namespace MMCA.Common.UI.Tests.Services;

/// <summary>
/// Covers <see cref="HttpResultExecutor"/>, the half of the Result transport that converts the
/// absence of a response (a refused connection, a dropped socket, an unreadable body, a client
/// timeout) into a failed <see cref="Result"/>. The two invariants that matter most: caller
/// cancellation is never swallowed, and a genuine programming fault keeps travelling as an
/// exception rather than being reported to the page as a transport failure.
/// </summary>
public sealed class HttpResultExecutorTests
{
    private const string HttpFault = "http";
    private const string IoFault = "io";
    private const string JsonFault = "json";

    [Fact]
    public void ErrorCodes_AreStable()
    {
        // Pages branch on these codes (the messages are English by design), so they are contract.
        HttpResultExecutor.TransportErrorCode.Should().Be("Http.TransportFailure");
        HttpResultExecutor.TimeoutErrorCode.Should().Be("Http.Timeout");
    }

    // == Pass-through ==
    [Fact]
    public async Task ExecuteAsync_PassesASuccessThrough()
    {
        var success = Result.Success();

        var actual = await HttpResultExecutor.ExecuteAsync(() => Task.FromResult(success), CancellationToken.None);

        actual.Should().BeSameAs(success);
        actual.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_Generic_PassesASuccessValueThrough()
    {
        var success = Result.Success("payload");

        var actual = await HttpResultExecutor.ExecuteAsync(
            () => Task.FromResult(success),
            CancellationToken.None);

        actual.Should().BeSameAs(success);
        actual.Value.Should().Be("payload");
    }

    [Fact]
    public async Task ExecuteAsync_PassesAFailureThroughUnchanged()
    {
        Error error = new("Order.NotFound", "That order no longer exists.", ErrorType.NotFound);
        var failure = Result.Failure(error);

        var actual = await HttpResultExecutor.ExecuteAsync(() => Task.FromResult(failure), CancellationToken.None);

        actual.Should().BeSameAs(failure);
        actual.Errors.Should().ContainSingle().Which.Should().BeSameAs(error);
    }

    [Fact]
    public async Task ExecuteAsync_Generic_PassesAFailureThroughUnchanged()
    {
        Error error = new("Order.Forbidden", "Not yours.", ErrorType.Forbidden);
        var failure = Result.Failure<int>(error);

        var actual = await HttpResultExecutor.ExecuteAsync(
            () => Task.FromResult(failure),
            CancellationToken.None);

        actual.Should().BeSameAs(failure);
        actual.Errors.Should().ContainSingle().Which.Should().BeSameAs(error);
    }

    // == Transport faults ==
    [Theory]
    [InlineData(HttpFault)]
    [InlineData(IoFault)]
    [InlineData(JsonFault)]
    public async Task ExecuteAsync_ConvertsATransportFaultIntoASingleUnexpectedFailure(string kind)
    {
        Exception fault = TransportFault(kind);

        var actual = await HttpResultExecutor.ExecuteAsync(
            () => throw fault,
            CancellationToken.None);

        actual.IsFailure.Should().BeTrue();
        var error = actual.Errors.Should().ContainSingle().Subject;
        error.Code.Should().Be(HttpResultExecutor.TransportErrorCode);
        error.Type.Should().Be(ErrorType.Unexpected);
    }

    [Theory]
    [InlineData(HttpFault)]
    [InlineData(IoFault)]
    [InlineData(JsonFault)]
    public async Task ExecuteAsync_Generic_ConvertsATransportFaultIntoASingleUnexpectedFailure(string kind)
    {
        Exception fault = TransportFault(kind);

        var actual = await HttpResultExecutor.ExecuteAsync<string>(
            () => throw fault,
            CancellationToken.None);

        actual.IsFailure.Should().BeTrue();
        actual.Value.Should().BeNull();
        var error = actual.Errors.Should().ContainSingle().Subject;
        error.Code.Should().Be(HttpResultExecutor.TransportErrorCode);
        error.Type.Should().Be(ErrorType.Unexpected);
    }

    [Fact]
    public async Task ExecuteAsync_KeepsTheExceptionTextOnSourceAndOutOfTheMessage()
    {
        string diagnostic = "No connection could be made because the target machine actively refused it.";

        var actual = await HttpResultExecutor.ExecuteAsync(
            () => throw new HttpRequestException(diagnostic),
            CancellationToken.None);

        var error = actual.Errors.Should().ContainSingle().Subject;
        error.Source.Should().Be(diagnostic);
        error.Message.Should().NotContain(
            diagnostic,
            "the raw exception text is diagnostic detail, neither localizable nor safe to render");
        error.Message.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ExecuteAsync_ConvertsAFaultedTaskTheSameWayAsASynchronousThrow()
    {
        // The realistic shape: the exception surfaces on the await, not on the call.
        var actual = await HttpResultExecutor.ExecuteAsync(
            () => Task.FromException<Result>(new HttpRequestException("socket closed")),
            CancellationToken.None);

        actual.Errors.Should().ContainSingle().Which.Code.Should().Be(HttpResultExecutor.TransportErrorCode);
    }

    // == Client timeout (cancellation with the caller's token still live) ==
    [Fact]
    public async Task ExecuteAsync_ConvertsAClientTimeoutIntoATimeoutFailure()
    {
        var actual = await HttpResultExecutor.ExecuteAsync(
            () => throw new OperationCanceledException(),
            CancellationToken.None);

        actual.IsFailure.Should().BeTrue();
        var error = actual.Errors.Should().ContainSingle().Subject;
        error.Code.Should().Be(HttpResultExecutor.TimeoutErrorCode);
        error.Type.Should().Be(ErrorType.Unexpected);
        error.Source.Should().BeNull("a timeout has no exception text worth keeping");
    }

    [Fact]
    public async Task ExecuteAsync_Generic_ConvertsAClientTimeoutIntoATimeoutFailure()
    {
        var actual = await HttpResultExecutor.ExecuteAsync<string>(
            () => throw new OperationCanceledException(),
            CancellationToken.None);

        actual.IsFailure.Should().BeTrue();
        actual.Errors.Should().ContainSingle().Which.Code.Should().Be(HttpResultExecutor.TimeoutErrorCode);
    }

    [Fact]
    public async Task ExecuteAsync_TreatsATaskCanceledExceptionAsATimeout_WhenTheCallerTokenIsLive()
    {
        // HttpClient's own timeout raises TaskCanceledException with the caller's token NOT cancelled.
        using var cts = new CancellationTokenSource();

        var actual = await HttpResultExecutor.ExecuteAsync(
            () => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout."),
            cts.Token);

        actual.Errors.Should().ContainSingle().Which.Code.Should().Be(HttpResultExecutor.TimeoutErrorCode);
    }

    // == Caller cancellation always propagates ==
    [Fact]
    public async Task ExecuteAsync_RethrowsCancellation_WhenTheCallerTokenIsCancelled()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        Func<Task> act = () => HttpResultExecutor.ExecuteAsync(
            () => throw new OperationCanceledException(cts.Token),
            cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>(
            "a page owns its own cancellation and must not have it reported back as an error to render");
    }

    [Fact]
    public async Task ExecuteAsync_Generic_RethrowsCancellation_WhenTheCallerTokenIsCancelled()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        Func<Task> act = () => HttpResultExecutor.ExecuteAsync<string>(
            () => throw new OperationCanceledException(cts.Token),
            cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ExecuteAsync_RethrowsATaskCanceledException_WhenTheCallerTokenIsCancelled()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        Func<Task> act = () => HttpResultExecutor.ExecuteAsync(
            () => Task.FromCanceled<Result>(cts.Token),
            cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // == Genuine faults keep travelling ==
    [Fact]
    public async Task ExecuteAsync_LetsAnUnrelatedExceptionPropagate()
    {
        Func<Task> act = () => HttpResultExecutor.ExecuteAsync(
            () => throw new InvalidOperationException("a programming fault, not a transport one"),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task ExecuteAsync_Generic_LetsAnUnrelatedExceptionPropagate()
    {
        Func<Task> act = () => HttpResultExecutor.ExecuteAsync<int>(
            () => throw new NotSupportedException("still a bug"),
            CancellationToken.None);

        await act.Should().ThrowAsync<NotSupportedException>();
    }

    // == Guards ==
    [Fact]
    public async Task ExecuteAsync_Throws_ForANullOperation()
    {
        Func<Task> act = () => HttpResultExecutor.ExecuteAsync(null!, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task ExecuteAsync_Generic_Throws_ForANullOperation()
    {
        Func<Task> act = () => HttpResultExecutor.ExecuteAsync<string>(null!, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    private static Exception TransportFault(string kind) => kind switch
    {
        HttpFault => new HttpRequestException("connection refused"),
        IoFault => new IOException("the response stream ended unexpectedly"),
        JsonFault => new JsonException("unexpected token at position 0"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown transport fault kind."),
    };
}

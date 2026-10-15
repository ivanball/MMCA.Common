using System.Data.Common;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using MMCA.Common.API.Middleware;
using Moq;

namespace MMCA.Common.API.Tests.Middleware;

/// <summary>
/// 499 means the client went away (L64). A cancellation the client did not cause, such as a
/// downstream HttpClient timeout surfacing as a TaskCanceledException, is a server error and must
/// fall through to the generic handler instead of being reported as a benign disconnect.
/// </summary>
public sealed class OperationCanceledExceptionHandlerTests
{
    [Fact]
    public async Task TryHandleAsync_WhenTheClientIsStillConnected_LeavesTheCancellationToTheGenericHandler()
    {
        var sut = CreateSut();
        var httpContext = new DefaultHttpContext();

        bool handled = await sut.TryHandleAsync(httpContext, new TaskCanceledException(), CancellationToken.None);

        handled.Should().BeFalse();
        httpContext.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task TryHandleAsync_WhenTheClientDisconnected_Answers499()
    {
        var sut = CreateSut();
        var httpContext = new DefaultHttpContext { RequestAborted = new CancellationToken(canceled: true) };

        bool handled = await sut.TryHandleAsync(httpContext, new OperationCanceledException(), CancellationToken.None);

        handled.Should().BeTrue();
        httpContext.Response.StatusCode.Should().Be(StatusCodes.Status499ClientClosedRequest);
    }

    [Fact]
    public async Task TryHandleAsync_WhenTheClientDisconnectedDuringAQuery_AnswersTheProviderCancelWith499()
    {
        // SqlClient can report a cancelled command as a SqlException ("Operation cancelled by
        // user") rather than an OperationCanceledException. After the client has gone that is the
        // same disconnect, and it used to reach the generic handler as an Error-level unhandled 500.
        var sut = CreateSut();
        var httpContext = new DefaultHttpContext { RequestAborted = new CancellationToken(canceled: true) };

        bool handled = await sut.TryHandleAsync(httpContext, new ProviderCancelException(), CancellationToken.None);

        handled.Should().BeTrue();
        httpContext.Response.StatusCode.Should().Be(StatusCodes.Status499ClientClosedRequest);
    }

    [Fact]
    public async Task TryHandleAsync_WhenTheClientIsStillConnected_LeavesADatabaseFailureToTheGenericHandler()
    {
        var sut = CreateSut();
        var httpContext = new DefaultHttpContext();

        bool handled = await sut.TryHandleAsync(httpContext, new ProviderCancelException(), CancellationToken.None);

        handled.Should().BeFalse();
        httpContext.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task TryHandleAsync_WhenTheClientDisconnected_LeavesAnUnrelatedFailureToTheGenericHandler()
    {
        // Only cancellation-shaped failures are attributed to the disconnect; any other exception
        // that coincides with it is still a server fault.
        var sut = CreateSut();
        var httpContext = new DefaultHttpContext { RequestAborted = new CancellationToken(canceled: true) };

        bool handled = await sut.TryHandleAsync(httpContext, new InvalidOperationException(), CancellationToken.None);

        handled.Should().BeFalse();
    }

    private static OperationCanceledExceptionHandler CreateSut()
    {
        var problemDetailsService = new Mock<IProblemDetailsService>();
        problemDetailsService.Setup(x => x.TryWriteAsync(It.IsAny<ProblemDetailsContext>())).ReturnsAsync(true);
        return new OperationCanceledExceptionHandler(
            problemDetailsService.Object,
            NullLogger<OperationCanceledExceptionHandler>.Instance);
    }

    /// <summary>Stands in for a provider's cancel report (SqlClient's SqlException is sealed).</summary>
    private sealed class ProviderCancelException : DbException
    {
        public ProviderCancelException()
            : base("Operation cancelled by user.")
        {
        }

        public ProviderCancelException(string message)
            : base(message)
        {
        }

        public ProviderCancelException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}

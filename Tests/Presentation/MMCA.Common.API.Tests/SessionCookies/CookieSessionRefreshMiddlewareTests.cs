using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.API.SessionCookies;
using Moq;

namespace MMCA.Common.API.Tests.SessionCookies;

/// <summary>
/// Verifies <see cref="CookieSessionRefreshMiddleware"/> attempts the validate-or-refresh only
/// on full-page navigations (GET + <c>Accept: text/html</c>), never blocks the pipeline on a
/// failed refresh, and always invokes the next delegate.
/// </summary>
public sealed class CookieSessionRefreshMiddlewareTests
{
    // ── Gating ──
    [Fact]
    public async Task InvokeAsync_GetHtmlNavigation_AttemptsRefreshAndCallsNext()
    {
        var (sut, refresher, nextSpy) = CreateSut();
        var context = CreateContext(HttpMethods.Get, "text/html");

        await sut.InvokeAsync(context);

        refresher.Verify(x => x.GetOrRefreshAsync(context, context.RequestAborted), Times.Once);
        nextSpy.Called.Should().BeTrue();
    }

    [Fact]
    public async Task InvokeAsync_BrowserStyleAcceptList_AttemptsRefresh()
    {
        var (sut, refresher, _) = CreateSut();
        var context = CreateContext(HttpMethods.Get, "application/xhtml+xml, TEXT/HTML;q=0.9, */*;q=0.8");

        await sut.InvokeAsync(context);

        refresher.Verify(
            x => x.GetOrRefreshAsync(It.IsAny<HttpContext>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "text/html is matched case-insensitively anywhere in the Accept header");
    }

    [Fact]
    public async Task InvokeAsync_GetWithoutHtmlAccept_SkipsRefreshButCallsNext()
    {
        var (sut, refresher, nextSpy) = CreateSut();
        var context = CreateContext(HttpMethods.Get, "application/json");

        await sut.InvokeAsync(context);

        refresher.Verify(
            x => x.GetOrRefreshAsync(It.IsAny<HttpContext>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "API/XHR calls must not trigger a server-side refresh");
        nextSpy.Called.Should().BeTrue();
    }

    [Fact]
    public async Task InvokeAsync_GetWithNoAcceptHeader_SkipsRefresh()
    {
        var (sut, refresher, nextSpy) = CreateSut();
        var context = CreateContext(HttpMethods.Get, accept: null);

        await sut.InvokeAsync(context);

        refresher.Verify(
            x => x.GetOrRefreshAsync(It.IsAny<HttpContext>(), It.IsAny<CancellationToken>()),
            Times.Never);
        nextSpy.Called.Should().BeTrue();
    }

    [Fact]
    public async Task InvokeAsync_PostWithHtmlAccept_SkipsRefresh()
    {
        var (sut, refresher, nextSpy) = CreateSut();
        var context = CreateContext(HttpMethods.Post, "text/html");

        await sut.InvokeAsync(context);

        refresher.Verify(
            x => x.GetOrRefreshAsync(It.IsAny<HttpContext>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "only GET navigations qualify");
        nextSpy.Called.Should().BeTrue();
    }

    // ── Failure passthrough ──
    [Fact]
    public async Task InvokeAsync_WhenRefreshReturnsNull_StillCallsNext()
    {
        var (sut, refresher, nextSpy) = CreateSut();
        refresher
            .Setup(x => x.GetOrRefreshAsync(It.IsAny<HttpContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SessionTokenResult?)null);
        var context = CreateContext(HttpMethods.Get, "text/html");

        await sut.InvokeAsync(context);

        nextSpy.Called.Should().BeTrue("a failed refresh must not short-circuit the pipeline");
    }

    [Fact]
    public async Task InvokeAsync_NullContext_ThrowsArgumentNullException()
    {
        var (sut, _, _) = CreateSut();

        Func<Task> act = () => sut.InvokeAsync(null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // ── Registration extension ──
    [Fact]
    public void UseCookieSessionRefresh_NullApp_ThrowsArgumentNullException()
    {
        Action act = () => CookieSessionRefreshMiddlewareExtensions.UseCookieSessionRefresh(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task UseCookieSessionRefresh_BuildsPipelineThatInvokesTheMiddleware()
    {
        var refresher = new Mock<ICookieSessionRefresher>();
        ServiceProvider services = new ServiceCollection()
            .AddSingleton(refresher.Object)
            .BuildServiceProvider();
        var app = new ApplicationBuilder(services);
        RequestDelegate pipeline = app.UseCookieSessionRefresh().Build();
        var context = CreateContext(HttpMethods.Get, "text/html");
        context.RequestServices = services;

        await pipeline(context);

        refresher.Verify(
            x => x.GetOrRefreshAsync(It.IsAny<HttpContext>(), It.IsAny<CancellationToken>()),
            Times.Once);

        await services.DisposeAsync();
    }

    // ── Helpers ──
    private sealed class NextDelegateSpy
    {
        public bool Called { get; private set; }

        public HttpContext? LastContext { get; private set; }

        public Task InvokeAsync(HttpContext context)
        {
            Called = true;
            LastContext = context;
            return Task.CompletedTask;
        }
    }

    private static (CookieSessionRefreshMiddleware Sut, Mock<ICookieSessionRefresher> Refresher, NextDelegateSpy NextSpy) CreateSut()
    {
        var refresher = new Mock<ICookieSessionRefresher>();
        var nextSpy = new NextDelegateSpy();
        var sut = new CookieSessionRefreshMiddleware(nextSpy.InvokeAsync, refresher.Object);
        return (sut, refresher, nextSpy);
    }

    private static DefaultHttpContext CreateContext(string method, string? accept)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        if (accept is not null)
        {
            context.Request.Headers.Accept = accept;
        }

        return context;
    }
}

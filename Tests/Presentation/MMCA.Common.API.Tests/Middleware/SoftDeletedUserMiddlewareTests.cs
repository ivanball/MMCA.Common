using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using MMCA.Common.API.Middleware;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Interfaces.Infrastructure;
using Moq;

namespace MMCA.Common.API.Tests.Middleware;

public sealed class SoftDeletedUserMiddlewareTests
{
    private readonly Mock<ICurrentUserService> _currentUserService = new();
    private readonly Mock<ICacheService> _cacheService = new();
    private readonly Mock<ISoftDeletedUserValidator> _validator = new();

    // ── Anonymous request passes through ──
    [Fact]
    public async Task InvokeAsync_AnonymousRequest_PassesThrough()
    {
        _currentUserService.Setup(s => s.UserId).Returns((int?)null);
        var nextCalled = false;
        var sut = new SoftDeletedUserMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        await sut.InvokeAsync(new DefaultHttpContext(), _currentUserService.Object, _cacheService.Object, _validator.Object);

        nextCalled.Should().BeTrue();
    }

    // ── Non-deleted user passes through ──
    [Fact]
    public async Task InvokeAsync_NonDeletedUser_PassesThrough()
    {
        _currentUserService.Setup(s => s.UserId).Returns(1);
        _cacheService.Setup(c => c.GetAsync<bool?>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((bool?)null);
        _validator.Setup(v => v.IsUserSoftDeletedAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var nextCalled = false;
        var sut = new SoftDeletedUserMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        await sut.InvokeAsync(new DefaultHttpContext(), _currentUserService.Object, _cacheService.Object, _validator.Object);

        nextCalled.Should().BeTrue();
    }

    // ── Deleted user returns 401 ──
    [Fact]
    public async Task InvokeAsync_DeletedUser_Returns401()
    {
        _currentUserService.Setup(s => s.UserId).Returns(1);
        _cacheService.Setup(c => c.GetAsync<bool?>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((bool?)null);
        _validator.Setup(v => v.IsUserSoftDeletedAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var context = new DefaultHttpContext();
        var sut = new SoftDeletedUserMiddleware(_ => Task.CompletedTask);

        await sut.InvokeAsync(context, _currentUserService.Object, _cacheService.Object, _validator.Object);

        context.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }

    // ── Cached deleted user returns 401 without DB call ──
    [Fact]
    public async Task InvokeAsync_CachedDeletedUser_Returns401WithoutDbCall()
    {
        _currentUserService.Setup(s => s.UserId).Returns(1);
        _cacheService.Setup(c => c.GetAsync<bool?>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var context = new DefaultHttpContext();
        var sut = new SoftDeletedUserMiddleware(_ => Task.CompletedTask);

        await sut.InvokeAsync(context, _currentUserService.Object, _cacheService.Object, _validator.Object);

        context.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        _validator.Verify(
            v => v.IsUserSoftDeletedAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── Cached non-deleted user passes without DB call ──
    [Fact]
    public async Task InvokeAsync_CachedNonDeletedUser_PassesThroughWithoutDbCall()
    {
        _currentUserService.Setup(s => s.UserId).Returns(1);
        _cacheService.Setup(c => c.GetAsync<bool?>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var nextCalled = false;
        var sut = new SoftDeletedUserMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        await sut.InvokeAsync(new DefaultHttpContext(), _currentUserService.Object, _cacheService.Object, _validator.Object);

        nextCalled.Should().BeTrue();
        _validator.Verify(
            v => v.IsUserSoftDeletedAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}

using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
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

    /// <summary>
    /// Builds a <see cref="DefaultHttpContext"/> whose <see cref="HttpContext.RequestServices"/>
    /// can resolve <see cref="ISoftDeletedUserValidator"/>. Pass <c>includeValidator: false</c>
    /// to simulate a service that does not host Identity (no validator registered).
    /// </summary>
    private DefaultHttpContext CreateContext(bool includeValidator = true)
    {
        var services = new ServiceCollection();
        if (includeValidator)
        {
            services.AddSingleton(_validator.Object);
        }

        return new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
        };
    }

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

        await sut.InvokeAsync(CreateContext(), _currentUserService.Object, _cacheService.Object);

        nextCalled.Should().BeTrue();
    }

    // ── No validator registered (e.g. Catalog service) — authenticated request passes through ──
    [Fact]
    public async Task InvokeAsync_NoValidatorRegistered_PassesThrough()
    {
        _currentUserService.Setup(s => s.UserId).Returns(1);
        var nextCalled = false;
        var sut = new SoftDeletedUserMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        await sut.InvokeAsync(CreateContext(includeValidator: false), _currentUserService.Object, _cacheService.Object);

        nextCalled.Should().BeTrue();
        _cacheService.Verify(
            c => c.GetAsync<bool?>(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
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

        await sut.InvokeAsync(CreateContext(), _currentUserService.Object, _cacheService.Object);

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
        var context = CreateContext();
        var sut = new SoftDeletedUserMiddleware(_ => Task.CompletedTask);

        await sut.InvokeAsync(context, _currentUserService.Object, _cacheService.Object);

        context.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }

    // ── Cached deleted user returns 401 without DB call ──
    [Fact]
    public async Task InvokeAsync_CachedDeletedUser_Returns401WithoutDbCall()
    {
        _currentUserService.Setup(s => s.UserId).Returns(1);
        _cacheService.Setup(c => c.GetAsync<bool?>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var context = CreateContext();
        var sut = new SoftDeletedUserMiddleware(_ => Task.CompletedTask);

        await sut.InvokeAsync(context, _currentUserService.Object, _cacheService.Object);

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

        await sut.InvokeAsync(CreateContext(), _currentUserService.Object, _cacheService.Object);

        nextCalled.Should().BeTrue();
        _validator.Verify(
            v => v.IsUserSoftDeletedAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}

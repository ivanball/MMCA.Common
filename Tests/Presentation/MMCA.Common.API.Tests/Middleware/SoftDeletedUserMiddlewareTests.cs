using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MMCA.Common.API.Middleware;
using MMCA.Common.Application.Auth;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Interfaces.Infrastructure.Auth;
using Moq;

namespace MMCA.Common.API.Tests.Middleware;

public sealed class SoftDeletedUserMiddlewareTests
{
    private const int UserId = 1;

    private static readonly string CacheKey = SoftDeletedUserCache.KeyFor(UserId);

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

        await InvokeAsync(sut, CreateContext());

        nextCalled.Should().BeTrue();
    }

    // ── No validator registered (e.g. Catalog service): authenticated request passes through ──
    [Fact]
    public async Task InvokeAsync_NoValidatorRegistered_PassesThrough()
    {
        _currentUserService.Setup(s => s.UserId).Returns(UserId);
        var nextCalled = false;
        var sut = new SoftDeletedUserMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        await InvokeAsync(sut, CreateContext(includeValidator: false));

        nextCalled.Should().BeTrue();
        _cacheService.Verify(
            c => c.GetAsync<bool?>(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── Non-deleted user passes through ──
    [Fact]
    public async Task InvokeAsync_NonDeletedUser_PassesThrough()
    {
        _currentUserService.Setup(s => s.UserId).Returns(UserId);
        _cacheService.Setup(c => c.GetAsync<bool?>(CacheKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync((bool?)null);
        _validator.Setup(v => v.IsUserSoftDeletedAsync(UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var nextCalled = false;
        var sut = new SoftDeletedUserMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        await InvokeAsync(sut, CreateContext());

        nextCalled.Should().BeTrue();
        _cacheService.Verify(
            c => c.SetAsync(CacheKey, false, SoftDeletedUserCache.MarkerDuration, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── Deleted user returns 401 ──
    [Fact]
    public async Task InvokeAsync_DeletedUser_Returns401()
    {
        _currentUserService.Setup(s => s.UserId).Returns(UserId);
        _cacheService.Setup(c => c.GetAsync<bool?>(CacheKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync((bool?)null);
        _validator.Setup(v => v.IsUserSoftDeletedAsync(UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var context = CreateContext();
        var sut = new SoftDeletedUserMiddleware(_ => Task.CompletedTask);

        await InvokeAsync(sut, context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }

    // ── Cached deleted user returns 401 without DB call ──
    [Fact]
    public async Task InvokeAsync_CachedDeletedUser_Returns401WithoutDbCall()
    {
        _currentUserService.Setup(s => s.UserId).Returns(UserId);
        _cacheService.Setup(c => c.GetAsync<bool?>(CacheKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var context = CreateContext();
        var sut = new SoftDeletedUserMiddleware(_ => Task.CompletedTask);

        await InvokeAsync(sut, context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        _validator.Verify(
            v => v.IsUserSoftDeletedAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── Cached non-deleted user passes without DB call ──
    [Fact]
    public async Task InvokeAsync_CachedNonDeletedUser_PassesThroughWithoutDbCall()
    {
        _currentUserService.Setup(s => s.UserId).Returns(UserId);
        _cacheService.Setup(c => c.GetAsync<bool?>(CacheKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var nextCalled = false;
        var sut = new SoftDeletedUserMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        await InvokeAsync(sut, CreateContext());

        nextCalled.Should().BeTrue();
        _validator.Verify(
            v => v.IsUserSoftDeletedAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── Fail open: cache outage falls back to the validator query ──
    [Fact]
    public async Task InvokeAsync_CacheReadThrowsAndUserIsDeleted_Returns401()
    {
        _currentUserService.Setup(s => s.UserId).Returns(UserId);
        _cacheService.Setup(c => c.GetAsync<bool?>(CacheKey, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("cache down"));
        _validator.Setup(v => v.IsUserSoftDeletedAsync(UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var context = CreateContext();
        var sut = new SoftDeletedUserMiddleware(_ => Task.CompletedTask);

        await InvokeAsync(sut, context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        _validator.Verify(
            v => v.IsUserSoftDeletedAsync(UserId, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task InvokeAsync_CacheReadThrowsAndUserIsLive_PassesThroughWithoutWritingTheCache()
    {
        _currentUserService.Setup(s => s.UserId).Returns(UserId);
        _cacheService.Setup(c => c.GetAsync<bool?>(CacheKey, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("cache down"));
        _validator.Setup(v => v.IsUserSoftDeletedAsync(UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var nextCalled = false;
        var sut = new SoftDeletedUserMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        await InvokeAsync(sut, CreateContext());

        nextCalled.Should().BeTrue();
        _cacheService.Verify(
            c => c.SetAsync(
                It.IsAny<string>(),
                It.IsAny<bool>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── Fail open: cache AND validator both unavailable ──
    [Fact]
    public async Task InvokeAsync_CacheAndValidatorBothThrow_PassesThrough()
    {
        _currentUserService.Setup(s => s.UserId).Returns(UserId);
        _cacheService.Setup(c => c.GetAsync<bool?>(CacheKey, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("cache down"));
        _validator.Setup(v => v.IsUserSoftDeletedAsync(UserId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database down"));
        var nextCalled = false;
        var context = CreateContext();
        var sut = new SoftDeletedUserMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        await InvokeAsync(sut, context);

        nextCalled.Should().BeTrue();
        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    // ── Fail open: only the validator is unavailable ──
    [Fact]
    public async Task InvokeAsync_ValidatorThrows_PassesThrough()
    {
        _currentUserService.Setup(s => s.UserId).Returns(UserId);
        _cacheService.Setup(c => c.GetAsync<bool?>(CacheKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync((bool?)null);
        _validator.Setup(v => v.IsUserSoftDeletedAsync(UserId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database down"));
        var nextCalled = false;
        var context = CreateContext();
        var sut = new SoftDeletedUserMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        await InvokeAsync(sut, context);

        nextCalled.Should().BeTrue();
        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    // ── Fail open: a failed cache write does not fail the request ──
    [Fact]
    public async Task InvokeAsync_CacheWriteThrows_PassesThrough()
    {
        _currentUserService.Setup(s => s.UserId).Returns(UserId);
        _cacheService.Setup(c => c.GetAsync<bool?>(CacheKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync((bool?)null);
        _cacheService.Setup(c => c.SetAsync(
                CacheKey,
                It.IsAny<bool>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("cache down"));
        _validator.Setup(v => v.IsUserSoftDeletedAsync(UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var nextCalled = false;
        var context = CreateContext();
        var sut = new SoftDeletedUserMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        await InvokeAsync(sut, context);

        nextCalled.Should().BeTrue();
        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    // ── A deleted user is still rejected when only the cache write fails ──
    [Fact]
    public async Task InvokeAsync_CacheWriteThrowsForDeletedUser_Returns401()
    {
        _currentUserService.Setup(s => s.UserId).Returns(UserId);
        _cacheService.Setup(c => c.GetAsync<bool?>(CacheKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync((bool?)null);
        _cacheService.Setup(c => c.SetAsync(
                CacheKey,
                It.IsAny<bool>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("cache down"));
        _validator.Setup(v => v.IsUserSoftDeletedAsync(UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var context = CreateContext();
        var sut = new SoftDeletedUserMiddleware(_ => Task.CompletedTask);

        await InvokeAsync(sut, context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }

    // ── Helpers ──

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

    /// <summary>
    /// Invokes the middleware with the shared mocks and a no-op logger (the logger is a
    /// per-invoke injected parameter, so every call has to supply one).
    /// </summary>
    private Task InvokeAsync(SoftDeletedUserMiddleware sut, HttpContext context) =>
        sut.InvokeAsync(
            context,
            _currentUserService.Object,
            _cacheService.Object,
            NullLogger<SoftDeletedUserMiddleware>.Instance);
}

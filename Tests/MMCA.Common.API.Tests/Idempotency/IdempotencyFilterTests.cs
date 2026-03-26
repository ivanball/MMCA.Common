using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.API.Idempotency;
using MMCA.Common.Application.Interfaces;
using Moq;

namespace MMCA.Common.API.Tests.Idempotency;

public sealed class IdempotencyFilterTests
{
    private static (ActionExecutingContext Context, Mock<ICacheService> Cache) CreateContext(
        string? idempotencyKey = null)
    {
        var cache = new Mock<ICacheService>();
        var services = new ServiceCollection();
        services.AddSingleton(cache.Object);
        var serviceProvider = services.BuildServiceProvider();

        var httpContext = new DefaultHttpContext { RequestServices = serviceProvider };
        if (idempotencyKey is not null)
            httpContext.Request.Headers[IdempotencyFilter.IdempotencyKeyHeader] = idempotencyKey;

        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        var context = new ActionExecutingContext(actionContext, [], new Dictionary<string, object?>(), null!);
        return (context, cache);
    }

    [Fact]
    public async Task OnActionExecutionAsync_NoIdempotencyKey_ExecutesNext()
    {
        var sut = new IdempotencyFilter();
        var (context, _) = CreateContext();
        var nextCalled = false;

        await sut.OnActionExecutionAsync(context, () =>
        {
            nextCalled = true;
            return Task.FromResult(new ActionExecutedContext(
                new ActionContext(context.HttpContext, context.RouteData, context.ActionDescriptor),
                [], null!));
        });

        nextCalled.Should().BeTrue();
    }

    [Fact]
    public async Task OnActionExecutionAsync_CachedResult_ReturnsCachedResponse()
    {
        var sut = new IdempotencyFilter();
        var (context, cache) = CreateContext("unique-key-1");

        var cachedRecord = new IdempotencyRecord(200, "{\"id\":1}");
        cache.Setup(x => x.GetAsync<IdempotencyRecord>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(cachedRecord);

        var nextCalled = false;

        await sut.OnActionExecutionAsync(context, () =>
        {
            nextCalled = true;
            return Task.FromResult(new ActionExecutedContext(
                new ActionContext(context.HttpContext, context.RouteData, context.ActionDescriptor),
                [], null!));
        });

        nextCalled.Should().BeFalse();
        context.Result.Should().BeOfType<ContentResult>();
        var contentResult = (ContentResult)context.Result!;
        contentResult.StatusCode.Should().Be(200);
        contentResult.Content.Should().Be("{\"id\":1}");
        context.HttpContext.Response.Headers["X-Idempotent-Replay"].ToString().Should().Be("true");
    }

    [Fact]
    public async Task OnActionExecutionAsync_NewRequest_ExecutesAndCaches()
    {
        var sut = new IdempotencyFilter();
        var (context, cache) = CreateContext("new-key-2");

        cache.Setup(x => x.GetAsync<IdempotencyRecord>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IdempotencyRecord?)null);

        await sut.OnActionExecutionAsync(context, () =>
        {
            var executedContext = new ActionExecutedContext(
                new ActionContext(context.HttpContext, context.RouteData, context.ActionDescriptor),
                [], null!)
            {
                Result = new ObjectResult(new { id = 42 }) { StatusCode = 201 }
            };
            return Task.FromResult(executedContext);
        });

        cache.Verify(x => x.SetAsync(
            It.IsAny<string>(),
            It.IsAny<IdempotencyRecord>(),
            It.IsAny<TimeSpan>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ConcurrentRequests_SameKey_OnlyOneExecutes()
    {
        var idempotencyKey = $"concurrent-test-{Guid.NewGuid()}";
        var cachedRecord = new IdempotencyRecord(200, "{\"id\":1}");
        var nextCallCount = 0;

        // Semaphore to hold the first next() call in progress while the second request starts
        using var holdFirstExecution = new SemaphoreSlim(0, 1);
        using var firstEnteredNext = new SemaphoreSlim(0, 1);

        // Build two independent contexts sharing the same cache mock
        var (context1, cache1) = CreateContext(idempotencyKey);
        var (context2, cache2) = CreateContext(idempotencyKey);

        var callCount = 0;

        // First call returns null (no cache), subsequent calls return the cached record
        // to simulate that the first request populated the cache
        cache1.Setup(x => x.GetAsync<IdempotencyRecord>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                var current = Interlocked.Increment(ref callCount);
                return current <= 2 ? null : cachedRecord;
            });

        cache2.Setup(x => x.GetAsync<IdempotencyRecord>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => cachedRecord);

        cache1.Setup(x => x.SetAsync(
                It.IsAny<string>(),
                It.IsAny<IdempotencyRecord>(),
                It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var filter1 = new IdempotencyFilter();
        var filter2 = new IdempotencyFilter();

        async Task<ActionExecutedContext> NextDelegate1()
        {
            Interlocked.Increment(ref nextCallCount);
            firstEnteredNext.Release();
            await holdFirstExecution.WaitAsync();
            return new ActionExecutedContext(
                new ActionContext(context1.HttpContext, context1.RouteData, context1.ActionDescriptor),
                [], null!)
            {
                Result = new ObjectResult(new { id = 1 }) { StatusCode = 200 }
            };
        }

        Task<ActionExecutedContext> NextDelegate2()
        {
            Interlocked.Increment(ref nextCallCount);
            var executedContext = new ActionExecutedContext(
                new ActionContext(context2.HttpContext, context2.RouteData, context2.ActionDescriptor),
                [], null!)
            {
                Result = new ObjectResult(new { id = 1 }) { StatusCode = 200 }
            };
            return Task.FromResult(executedContext);
        }

        // Launch both requests concurrently
        Task task1 = filter1.OnActionExecutionAsync(context1, NextDelegate1);

        // Wait for the first request to enter next() before starting the second
        await firstEnteredNext.WaitAsync();

        Task task2 = filter2.OnActionExecutionAsync(context2, NextDelegate2);

        // Release the first request to complete
        holdFirstExecution.Release();

        await Task.WhenAll(task1, task2);

        nextCallCount.Should().Be(1, "only one next() delegate should execute; the second should get the cached response");
    }

    [Fact]
    public async Task EmptyIdempotencyKey_ExecutesNormally()
    {
        var sut = new IdempotencyFilter();
        var (context, cache) = CreateContext("   ");
        var nextCalled = false;

        await sut.OnActionExecutionAsync(context, () =>
        {
            nextCalled = true;
            return Task.FromResult(new ActionExecutedContext(
                new ActionContext(context.HttpContext, context.RouteData, context.ActionDescriptor),
                [], null!));
        });

        nextCalled.Should().BeTrue("empty/whitespace idempotency key should be treated as absent");
        cache.Verify(
            x => x.GetAsync<IdempotencyRecord>(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "cache should not be consulted for empty idempotency key");
    }

    [Fact]
    public async Task NonObjectResult_NotCached()
    {
        var sut = new IdempotencyFilter();
        var idempotencyKey = $"non-object-result-{Guid.NewGuid()}";
        var (context, cache) = CreateContext(idempotencyKey);

        cache.Setup(x => x.GetAsync<IdempotencyRecord>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IdempotencyRecord?)null);

        await sut.OnActionExecutionAsync(context, () =>
        {
            var executedContext = new ActionExecutedContext(
                new ActionContext(context.HttpContext, context.RouteData, context.ActionDescriptor),
                [], null!)
            {
                Result = new RedirectResult("https://example.com")
            };
            return Task.FromResult(executedContext);
        });

        cache.Verify(
            x => x.SetAsync(
                It.IsAny<string>(),
                It.IsAny<IdempotencyRecord>(),
                It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>()),
            Times.Never,
            "non-ObjectResult responses should not be cached");
    }

    [Fact]
    public async Task CachedResponse_IncludesReplayHeader_OnDoubleCheckPath()
    {
        var sut = new IdempotencyFilter();
        var idempotencyKey = $"double-check-replay-{Guid.NewGuid()}";
        var (context, cache) = CreateContext(idempotencyKey);

        var cachedRecord = new IdempotencyRecord(200, "{\"replayed\":true}");
        var getCallCount = 0;

        // First GetAsync returns null (fast path misses), second returns cached record
        // (double-check inside the lock finds it — another request completed while waiting)
        cache.Setup(x => x.GetAsync<IdempotencyRecord>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                var current = Interlocked.Increment(ref getCallCount);
                return current == 1 ? null : cachedRecord;
            });

        var nextCalled = false;

        await sut.OnActionExecutionAsync(context, () =>
        {
            nextCalled = true;
            return Task.FromResult(new ActionExecutedContext(
                new ActionContext(context.HttpContext, context.RouteData, context.ActionDescriptor),
                [], null!));
        });

        nextCalled.Should().BeFalse("next should not execute when double-check finds a cached response");
        context.Result.Should().BeOfType<ContentResult>();
        var contentResult = (ContentResult)context.Result!;
        contentResult.StatusCode.Should().Be(200);
        contentResult.Content.Should().Be("{\"replayed\":true}");
        context.HttpContext.Response.Headers["X-Idempotent-Replay"].ToString()
            .Should().Be("true", "replayed responses from the double-check path must include the replay header");
    }
}

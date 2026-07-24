using System.Globalization;
using System.Security.Claims;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.API.Idempotency;
using MMCA.Common.Application.Interfaces;
using Moq;

namespace MMCA.Common.API.Tests.Idempotency;

public sealed class IdempotencyFilterTests
{
    private static (ActionExecutingContext Context, Mock<ICacheService> Cache) CreateContext(
        string? idempotencyKey = null,
        string? userId = null,
        string method = "POST",
        string? routeTemplate = null,
        Mock<ICacheService>? sharedCache = null)
    {
        var cache = sharedCache ?? new Mock<ICacheService>();
        var services = new ServiceCollection();
        services.AddSingleton(cache.Object);
        var serviceProvider = services.BuildServiceProvider();

        var httpContext = new DefaultHttpContext { RequestServices = serviceProvider };
        httpContext.Request.Method = method;
        if (idempotencyKey is not null)
            httpContext.Request.Headers[IdempotencyFilter.IdempotencyKeyHeader] = idempotencyKey;

        if (userId is not null)
        {
            httpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity([new Claim("user_id", userId)], "TestAuth"));
        }

        var descriptor = new ActionDescriptor();
        if (routeTemplate is not null)
            descriptor.AttributeRouteInfo = new AttributeRouteInfo { Template = routeTemplate };

        var actionContext = new ActionContext(httpContext, new RouteData(), descriptor);
        var context = new ActionExecutingContext(actionContext, [], new Dictionary<string, object?>(), null!);
        return (context, cache);
    }

    /// <summary>Runs the filter and returns the cache key it looked the record up under.</summary>
    private static async Task<string> CaptureCacheKeyAsync(ActionExecutingContext context, Mock<ICacheService> cache)
    {
        string? observedKey = null;
        cache.Setup(x => x.GetAsync<IdempotencyRecord>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, CancellationToken>((key, _) => observedKey ??= key)
            .ReturnsAsync((IdempotencyRecord?)null);

        await new IdempotencyFilter().OnActionExecutionAsync(context, () =>
            Task.FromResult(new ActionExecutedContext(
                new ActionContext(context.HttpContext, context.RouteData, context.ActionDescriptor),
                [], null!)));

        observedKey.Should().NotBeNull();
        return observedKey!;
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

    // ── Cache-key scoping ──
    // The key used to be the bare client-supplied header value, so two callers who chose the same
    // value shared an entry and one user's serialized response body was replayed to the other. The
    // key now folds in the caller, the HTTP method and the route template.
    [Fact]
    public async Task CacheKey_DiffersPerCaller_ForTheSameClientKey()
    {
        const string clientKey = "shared-client-key";
        var (alice, aliceCache) = CreateContext(clientKey, userId: "1", routeTemplate: "Orders");
        var (bob, bobCache) = CreateContext(clientKey, userId: "2", routeTemplate: "Orders");

        var aliceKey = await CaptureCacheKeyAsync(alice, aliceCache);
        var bobKey = await CaptureCacheKeyAsync(bob, bobCache);

        aliceKey.Should().NotBe(bobKey, "one caller's cached response must never be replayed to another");
    }

    [Fact]
    public async Task CacheKey_IsStable_ForTheSameCallerEndpointAndClientKey()
    {
        const string clientKey = "retry-key";
        var (first, firstCache) = CreateContext(clientKey, userId: "1", routeTemplate: "Orders");
        var (second, secondCache) = CreateContext(clientKey, userId: "1", routeTemplate: "Orders");

        var firstKey = await CaptureCacheKeyAsync(first, firstCache);
        var secondKey = await CaptureCacheKeyAsync(second, secondCache);

        firstKey.Should().Be(secondKey, "a genuine retry must still hit the same entry");
    }

    [Fact]
    public async Task CacheKey_DiffersPerEndpoint_ForTheSameCallerAndClientKey()
    {
        const string clientKey = "shared-client-key";
        var (orders, ordersCache) = CreateContext(clientKey, userId: "1", routeTemplate: "Orders");
        var (carts, cartsCache) = CreateContext(clientKey, userId: "1", routeTemplate: "ShoppingCarts");

        var ordersKey = await CaptureCacheKeyAsync(orders, ordersCache);
        var cartsKey = await CaptureCacheKeyAsync(carts, cartsCache);

        ordersKey.Should().NotBe(cartsKey, "services sharing one cache instance must not collide across endpoints");
    }

    [Fact]
    public async Task CacheKey_DiffersPerHttpMethod_ForTheSameCallerAndClientKey()
    {
        const string clientKey = "shared-client-key";
        var (post, postCache) = CreateContext(clientKey, userId: "1", method: "POST", routeTemplate: "Orders/{id}");
        var (put, putCache) = CreateContext(clientKey, userId: "1", method: "PUT", routeTemplate: "Orders/{id}");

        var postKey = await CaptureCacheKeyAsync(post, postCache);
        var putKey = await CaptureCacheKeyAsync(put, putCache);

        postKey.Should().NotBe(putKey);
    }

    [Fact]
    public async Task CacheKey_DiffersPerAnonymousCaller()
    {
        const string clientKey = "shared-client-key";
        var (first, firstCache) = CreateContext(clientKey, routeTemplate: "Orders");
        first.HttpContext.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("203.0.113.1");
        var (second, secondCache) = CreateContext(clientKey, routeTemplate: "Orders");
        second.HttpContext.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("203.0.113.2");

        var firstKey = await CaptureCacheKeyAsync(first, firstCache);
        var secondKey = await CaptureCacheKeyAsync(second, secondCache);

        firstKey.Should().NotBe(secondKey, "unauthenticated callers fall back to remote address scoping");
    }

    // ── Only successful responses are cached ──
    // Caching a failure replayed it for the whole 24-hour window, so a client retrying the same key
    // after a transient 500 kept receiving that 500 instead of the retry executing.
    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(409)]
    [InlineData(422)]
    [InlineData(500)]
    public async Task FailureResponse_NotCached(int statusCode)
    {
        var sut = new IdempotencyFilter();
        var (context, cache) = CreateContext(string.Create(CultureInfo.InvariantCulture, $"failure-{statusCode}-{Guid.NewGuid()}"));

        cache.Setup(x => x.GetAsync<IdempotencyRecord>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IdempotencyRecord?)null);

        await sut.OnActionExecutionAsync(context, () =>
        {
            var executedContext = new ActionExecutedContext(
                new ActionContext(context.HttpContext, context.RouteData, context.ActionDescriptor),
                [], null!)
            {
                Result = new ObjectResult(new ProblemDetails { Status = statusCode }) { StatusCode = statusCode }
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
            "a failed response must not be replayed for the retention window");
    }

    [Theory]
    [InlineData(200)]
    [InlineData(201)]
    [InlineData(202)]
    public async Task SuccessResponse_IsCached(int statusCode)
    {
        var sut = new IdempotencyFilter();
        var (context, cache) = CreateContext(string.Create(CultureInfo.InvariantCulture, $"success-{statusCode}-{Guid.NewGuid()}"));

        cache.Setup(x => x.GetAsync<IdempotencyRecord>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IdempotencyRecord?)null);

        await sut.OnActionExecutionAsync(context, () =>
        {
            var executedContext = new ActionExecutedContext(
                new ActionContext(context.HttpContext, context.RouteData, context.ActionDescriptor),
                [], null!)
            {
                Result = new ObjectResult(new { id = 42 }) { StatusCode = statusCode }
            };
            return Task.FromResult(executedContext);
        });

        cache.Verify(
            x => x.SetAsync(
                It.IsAny<string>(),
                It.Is<IdempotencyRecord>(r => r.StatusCode == statusCode),
                It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }
}

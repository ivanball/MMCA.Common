using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MMCA.Common.API.Idempotency;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Shared.Auth;
using Moq;

namespace MMCA.Common.API.Tests.Idempotency;

public sealed class IdempotencyFilterTests
{
    /// <summary>
    /// The hash the filter computes for a request that carries no body. Records built by hand in
    /// these tests use it so a replay of a body-less request compares equal.
    /// </summary>
    private static readonly string EmptyBodyHash = Convert.ToHexStringLower(SHA256.HashData([]));

    /// <summary>The filter takes an <c>ILogger</c> so a swallowed cache fault is still reported.</summary>
    private static IdempotencyFilter CreateSut() => new(NullLogger<IdempotencyFilter>.Instance);

    private static (ActionExecutingContext Context, Mock<ICacheService> Cache) CreateContext(
        string? idempotencyKey = null,
        string? userId = null,
        string method = "POST",
        string? routeTemplate = null,
        Mock<ICacheService>? sharedCache = null,
        IDistributedLock? distributedLock = null,
        string? body = null)
    {
        var cache = sharedCache ?? new Mock<ICacheService>();
        var services = new ServiceCollection();
        services.AddSingleton(cache.Object);
        if (distributedLock is not null)
            services.AddSingleton(distributedLock);

        var serviceProvider = services.BuildServiceProvider();

        var httpContext = new DefaultHttpContext { RequestServices = serviceProvider };
        httpContext.Request.Method = method;
        if (body is not null)
            httpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));

        if (idempotencyKey is not null)
            httpContext.Request.Headers[IdempotencyFilter.IdempotencyKeyHeader] = idempotencyKey;

        if (userId is not null)
        {
            httpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity([new Claim(AuthClaimTypes.Subject, userId)], "TestAuth"));
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

        await CreateSut().OnActionExecutionAsync(context, () =>
            Task.FromResult(new ActionExecutedContext(
                new ActionContext(context.HttpContext, context.RouteData, context.ActionDescriptor),
                [], null!)));

        observedKey.Should().NotBeNull();
        return observedKey!;
    }

    [Fact]
    public async Task OnActionExecutionAsync_NoIdempotencyKey_ExecutesNext()
    {
        var sut = CreateSut();
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
        var sut = CreateSut();
        var (context, cache) = CreateContext("unique-key-1");

        var cachedRecord = new IdempotencyRecord(200, "{\"id\":1}", EmptyBodyHash);
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
        var sut = CreateSut();
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
        var cachedRecord = new IdempotencyRecord(200, "{\"id\":1}", EmptyBodyHash);
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

        var filter1 = CreateSut();
        var filter2 = CreateSut();

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
        var sut = CreateSut();
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
        var sut = CreateSut();
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
        var sut = CreateSut();
        var idempotencyKey = $"double-check-replay-{Guid.NewGuid()}";
        var (context, cache) = CreateContext(idempotencyKey);

        var cachedRecord = new IdempotencyRecord(200, "{\"replayed\":true}", EmptyBodyHash);
        var getCallCount = 0;

        // First GetAsync returns null (fast path misses), second returns cached record
        // (double-check inside the lock finds it: another request completed while waiting)
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
        var sut = CreateSut();
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
        var sut = CreateSut();
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

    // ── Body-less successes ──
    // Only ObjectResult used to be stored, so every command answering NoContent() cached nothing at
    // all: a duplicate re-executed the action instead of replaying it, which is the whole point of
    // the filter. StatusCodeResults are now stored with an empty body.
    [Fact]
    public async Task NoContentResponse_IsCachedWithAnEmptyBody()
    {
        var sut = CreateSut();
        var (context, cache) = CreateContext($"no-content-{Guid.NewGuid()}");

        cache.Setup(x => x.GetAsync<IdempotencyRecord>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IdempotencyRecord?)null);

        await sut.OnActionExecutionAsync(context, () =>
            Task.FromResult(new ActionExecutedContext(
                new ActionContext(context.HttpContext, context.RouteData, context.ActionDescriptor),
                [], null!)
            {
                Result = new NoContentResult()
            }));

        cache.Verify(
            x => x.SetAsync(
                It.IsAny<string>(),
                It.Is<IdempotencyRecord>(r => r.StatusCode == 204 && r.ResponseBody.Length == 0),
                It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task FailureStatusCodeResult_NotCached()
    {
        var sut = CreateSut();
        var (context, cache) = CreateContext($"status-failure-{Guid.NewGuid()}");

        cache.Setup(x => x.GetAsync<IdempotencyRecord>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IdempotencyRecord?)null);

        await sut.OnActionExecutionAsync(context, () =>
            Task.FromResult(new ActionExecutedContext(
                new ActionContext(context.HttpContext, context.RouteData, context.ActionDescriptor),
                [], null!)
            {
                Result = new StatusCodeResult(409)
            }));

        cache.Verify(
            x => x.SetAsync(
                It.IsAny<string>(),
                It.IsAny<IdempotencyRecord>(),
                It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>()),
            Times.Never,
            "the 2xx-only rule applies to body-less results too");
    }

    [Fact]
    public async Task CachedEmptyBody_ReplaysAsAStatusCodeWithNoContentType()
    {
        var sut = CreateSut();
        var (context, cache) = CreateContext($"replay-no-content-{Guid.NewGuid()}");

        cache.Setup(x => x.GetAsync<IdempotencyRecord>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IdempotencyRecord(204, string.Empty, EmptyBodyHash));

        var nextCalled = false;

        await sut.OnActionExecutionAsync(context, () =>
        {
            nextCalled = true;
            return Task.FromResult(new ActionExecutedContext(
                new ActionContext(context.HttpContext, context.RouteData, context.ActionDescriptor),
                [], null!));
        });

        nextCalled.Should().BeFalse();
        context.Result.Should().BeOfType<StatusCodeResult>(
            "a response with no body must not be replayed as application/json content");
        ((StatusCodeResult)context.Result!).StatusCode.Should().Be(204);
        context.HttpContext.Response.Headers["X-Idempotent-Replay"].ToString().Should().Be("true");
    }

    // ── Cross-replica duplicates ──
    // The per-process stripe only serializes duplicates that land on the same replica, and both
    // deployed apps run more than one, so an IDistributedLock guards the execute-then-store window.
    [Fact]
    public async Task DistributedLock_WhenAcquired_ExecutesOnceAndReleases()
    {
        var sut = CreateSut();
        var handle = new TrackingHandle();
        var distributedLock = new Mock<IDistributedLock>();
        distributedLock
            .Setup(x => x.TryAcquireAsync(
                It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle);

        var (context, cache) = CreateContext($"lock-free-{Guid.NewGuid()}", distributedLock: distributedLock.Object);
        cache.Setup(x => x.GetAsync<IdempotencyRecord>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IdempotencyRecord?)null);

        var nextCalls = 0;

        await sut.OnActionExecutionAsync(context, () =>
        {
            nextCalls++;
            return Task.FromResult(new ActionExecutedContext(
                new ActionContext(context.HttpContext, context.RouteData, context.ActionDescriptor),
                [], null!)
            {
                Result = new ObjectResult(new { id = 1 }) { StatusCode = 201 }
            });
        });

        nextCalls.Should().Be(1);
        handle.Disposals.Should().Be(1, "the lock must be released even though the action succeeded");
        cache.Verify(
            x => x.SetAsync(
                It.IsAny<string>(),
                It.IsAny<IdempotencyRecord>(),
                It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>()),
            Times.Once,
            "the store has to happen inside the lock, before it is released");
    }

    [Fact]
    public async Task DistributedLock_WhenHeldElsewhereAndTheHolderStored_ReplaysWithoutExecuting()
    {
        var sut = CreateSut();
        var distributedLock = new Mock<IDistributedLock>();
        distributedLock
            .Setup(x => x.TryAcquireAsync(
                It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IAsyncDisposable?)null);

        var (context, cache) = CreateContext($"lock-held-{Guid.NewGuid()}", distributedLock: distributedLock.Object);

        // Fast path misses; by the time the acquire gives up, the other replica has stored its response.
        var getCalls = 0;
        cache.Setup(x => x.GetAsync<IdempotencyRecord>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => Interlocked.Increment(ref getCalls) == 1
                ? null
                : new IdempotencyRecord(200, "{\"id\":1}", EmptyBodyHash));

        var nextCalled = false;

        await sut.OnActionExecutionAsync(context, () =>
        {
            nextCalled = true;
            return Task.FromResult(new ActionExecutedContext(
                new ActionContext(context.HttpContext, context.RouteData, context.ActionDescriptor),
                [], null!));
        });

        nextCalled.Should().BeFalse("the other replica already ran this key; running it again is the double execution");
        context.Result.Should().BeOfType<ContentResult>();
        ((ContentResult)context.Result!).Content.Should().Be("{\"id\":1}");
        context.HttpContext.Response.Headers["X-Idempotent-Replay"].ToString().Should().Be("true");
    }

    [Fact]
    public async Task DistributedLock_WhenHeldElsewhereAndNothingStored_ReportsTheDuplicateInFlight()
    {
        var sut = CreateSut();
        var distributedLock = new Mock<IDistributedLock>();
        distributedLock
            .Setup(x => x.TryAcquireAsync(
                It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IAsyncDisposable?)null);

        var (context, cache) = CreateContext($"lock-inflight-{Guid.NewGuid()}", distributedLock: distributedLock.Object);
        cache.Setup(x => x.GetAsync<IdempotencyRecord>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IdempotencyRecord?)null);

        var nextCalled = false;

        await sut.OnActionExecutionAsync(context, () =>
        {
            nextCalled = true;
            return Task.FromResult(new ActionExecutedContext(
                new ActionContext(context.HttpContext, context.RouteData, context.ActionDescriptor),
                [], null!));
        });

        nextCalled.Should().BeFalse("executing while another replica holds the key is exactly the duplicate write");
        context.Result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        cache.Verify(
            x => x.SetAsync(
                It.IsAny<string>(),
                It.IsAny<IdempotencyRecord>(),
                It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task DistributedLock_IsAskedForABoundedWaitAndACrashGuardTtl()
    {
        var sut = CreateSut();
        var distributedLock = new Mock<IDistributedLock>();
        distributedLock
            .Setup(x => x.TryAcquireAsync(
                It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TrackingHandle());

        var (context, cache) = CreateContext($"lock-args-{Guid.NewGuid()}", distributedLock: distributedLock.Object);
        cache.Setup(x => x.GetAsync<IdempotencyRecord>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IdempotencyRecord?)null);

        await sut.OnActionExecutionAsync(context, () =>
            Task.FromResult(new ActionExecutedContext(
                new ActionContext(context.HttpContext, context.RouteData, context.ActionDescriptor),
                [], null!)));

        distributedLock.Verify(
            x => x.TryAcquireAsync(
                It.Is<string>(key => key.StartsWith("idempotency:", StringComparison.Ordinal)),
                It.Is<TimeSpan>(ttl => ttl > TimeSpan.Zero),
                It.Is<TimeSpan>(wait => wait > TimeSpan.Zero && wait < TimeSpan.FromMinutes(1)),
                It.IsAny<CancellationToken>()),
            Times.Once,
            "the lock is taken on the same key the response is cached under, with a finite wait");
    }

    // ── Request-body binding ──
    // A replay is decided by the key AND the payload: every stored record carries a hash of the body
    // that produced it, so reusing a key with a different payload is refused instead of being handed
    // the first response while the second write silently never runs.
    [Fact]
    public async Task SameRequestBody_ReplaysTheCachedResponse()
    {
        const string body = "{\"amount\":10}";
        var idempotencyKey = $"same-body-{Guid.NewGuid()}";

        var stored = await CaptureStoredRecordAsync(
            idempotencyKey, body, new ObjectResult(new { id = 42 }) { StatusCode = 201 });

        stored.Should().NotBeNull();

        var record = stored!;
        record.RequestBodyHash.Should().NotBeEmpty("the stored record binds the payload to the key");

        var (context, cache) = CreateContext(idempotencyKey, userId: "1", routeTemplate: "Orders", body: body);
        cache.Setup(x => x.GetAsync<IdempotencyRecord>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(record);

        var nextCalled = false;

        await CreateSut().OnActionExecutionAsync(context, () =>
        {
            nextCalled = true;
            return Task.FromResult(new ActionExecutedContext(
                new ActionContext(context.HttpContext, context.RouteData, context.ActionDescriptor),
                [], null!));
        });

        nextCalled.Should().BeFalse("an identical retry is the case idempotency exists to serve");
        context.Result.Should().BeOfType<ContentResult>()
            .Which.Content.Should().Be(record.ResponseBody);
        context.HttpContext.Response.Headers["X-Idempotent-Replay"].ToString().Should().Be("true");
    }

    [Fact]
    public async Task DifferentRequestBody_SameKey_IsRejectedAsUnprocessable()
    {
        var idempotencyKey = $"different-body-{Guid.NewGuid()}";

        var stored = await CaptureStoredRecordAsync(
            idempotencyKey, "{\"amount\":10}", new ObjectResult(new { id = 42 }) { StatusCode = 201 });

        var (context, cache) = CreateContext(
            idempotencyKey, userId: "1", routeTemplate: "Orders", body: "{\"amount\":99}");
        cache.Setup(x => x.GetAsync<IdempotencyRecord>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(stored);

        var nextCalled = false;

        await CreateSut().OnActionExecutionAsync(context, () =>
        {
            nextCalled = true;
            return Task.FromResult(new ActionExecutedContext(
                new ActionContext(context.HttpContext, context.RouteData, context.ActionDescriptor),
                [], null!));
        });

        nextCalled.Should().BeFalse();
        context.Result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status422UnprocessableEntity,
                "409 already means the original is in flight, so key reuse needs its own status");
        ((ProblemDetails)((ObjectResult)context.Result!).Value!).Detail
            .Should().Be("The Idempotency-Key was already used with a different request body.");
        context.HttpContext.Response.Headers.ContainsKey("X-Idempotent-Replay")
            .Should().BeFalse("a rejected reuse is not a replay");
    }

    [Fact]
    public async Task BodylessRequest_StoresAndReplays()
    {
        var idempotencyKey = $"bodyless-{Guid.NewGuid()}";

        var stored = await CaptureStoredRecordAsync(idempotencyKey, body: null, new NoContentResult());

        stored.Should().NotBeNull();
        stored!.RequestBodyHash.Should().Be(EmptyBodyHash, "a body-less request hashes the empty payload");

        var (context, cache) = CreateContext(idempotencyKey, userId: "1", routeTemplate: "Orders");
        cache.Setup(x => x.GetAsync<IdempotencyRecord>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(stored);

        var nextCalled = false;

        await CreateSut().OnActionExecutionAsync(context, () =>
        {
            nextCalled = true;
            return Task.FromResult(new ActionExecutedContext(
                new ActionContext(context.HttpContext, context.RouteData, context.ActionDescriptor),
                [], null!));
        });

        nextCalled.Should().BeFalse();
        context.Result.Should().BeOfType<StatusCodeResult>()
            .Which.StatusCode.Should().Be(204);
    }

    // ── Resource stage ──
    // Hashing the body means reading it, and the only point at which it can still be made
    // re-readable is before model binding. Buffering is not free, so it is enabled only for the
    // requests that actually carry a key.
    [Fact]
    public async Task ResourceStage_WithIdempotencyKey_EnablesBuffering()
    {
        var body = new NonSeekableStream(Encoding.UTF8.GetBytes("{\"amount\":10}"));
        var context = CreateResourceContext($"buffering-on-{Guid.NewGuid()}", body);

        await CreateSut().OnResourceExecutionAsync(
            context,
            () => Task.FromResult(new ResourceExecutedContext(context, context.Filters)));

        context.HttpContext.Request.Body.Should().NotBeSameAs(body);
        context.HttpContext.Request.Body.CanSeek.Should().BeTrue(
            "the action stage has to be able to rewind the body to hash it");
    }

    [Fact]
    public async Task ResourceStage_WithoutIdempotencyKey_LeavesTheBodyAlone()
    {
        var body = new NonSeekableStream(Encoding.UTF8.GetBytes("{\"amount\":10}"));
        var context = CreateResourceContext(idempotencyKey: null, body);

        await CreateSut().OnResourceExecutionAsync(
            context,
            () => Task.FromResult(new ResourceExecutedContext(context, context.Filters)));

        context.HttpContext.Request.Body.Should().BeSameAs(body,
            "ordinary traffic must not pay for buffering it never uses");
    }

    // ── Fail-open cache ──
    // Deduplication is an optimization over an at-least-once client retry. A cache outage must
    // degrade it, never turn every write endpoint carrying the attribute into a 500.
    [Fact]
    public async Task CacheReadFailure_StillExecutesTheActionAndKeepsItsResult()
    {
        var (context, cache) = CreateContext($"cache-read-fault-{Guid.NewGuid()}");
        cache.Setup(x => x.GetAsync<IdempotencyRecord>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("cache unreachable"));
        cache.Setup(x => x.SetAsync(
                It.IsAny<string>(),
                It.IsAny<IdempotencyRecord>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var expected = new ObjectResult(new { id = 7 }) { StatusCode = 201 };
        ActionExecutedContext? executed = null;

        await CreateSut().OnActionExecutionAsync(context, () =>
        {
            executed = new ActionExecutedContext(
                new ActionContext(context.HttpContext, context.RouteData, context.ActionDescriptor),
                [], null!)
            {
                Result = expected
            };
            return Task.FromResult(executed);
        });

        executed.Should().NotBeNull("a failing cache read must be treated as a miss, not as an error");
        executed!.Result.Should().BeSameAs(expected);
        context.Result.Should().BeNull("the filter must not short-circuit a request it could not deduplicate");
    }

    [Fact]
    public async Task CacheStoreFailure_StillReturnsTheActionResponse()
    {
        var (context, cache) = CreateContext($"cache-store-fault-{Guid.NewGuid()}");
        cache.Setup(x => x.GetAsync<IdempotencyRecord>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IdempotencyRecord?)null);
        cache.Setup(x => x.SetAsync(
                It.IsAny<string>(),
                It.IsAny<IdempotencyRecord>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("cache unreachable"));

        var expected = new ObjectResult(new { id = 9 }) { StatusCode = 201 };
        ActionExecutedContext? executed = null;

        await CreateSut().OnActionExecutionAsync(context, () =>
        {
            executed = new ActionExecutedContext(
                new ActionContext(context.HttpContext, context.RouteData, context.ActionDescriptor),
                [], null!)
            {
                Result = expected
            };
            return Task.FromResult(executed);
        });

        executed.Should().NotBeNull();
        executed!.Result.Should().BeSameAs(expected,
            "the write already happened; failing here would make the client retry it");
        context.Result.Should().BeNull();
    }

    /// <summary>
    /// Runs the filter once against a cache miss and returns the record it stored, so a test can
    /// replay a genuinely produced record instead of duplicating the hashing rule.
    /// </summary>
    private static async Task<IdempotencyRecord?> CaptureStoredRecordAsync(
        string idempotencyKey,
        string? body,
        IActionResult result)
    {
        var (context, cache) = CreateContext(idempotencyKey, userId: "1", routeTemplate: "Orders", body: body);

        IdempotencyRecord? stored = null;
        cache.Setup(x => x.GetAsync<IdempotencyRecord>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IdempotencyRecord?)null);
        cache.Setup(x => x.SetAsync(
                It.IsAny<string>(),
                It.IsAny<IdempotencyRecord>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, IdempotencyRecord, TimeSpan?, CancellationToken>((_, record, _, _) => stored = record)
            .Returns(Task.CompletedTask);

        await CreateSut().OnActionExecutionAsync(context, () =>
            Task.FromResult(new ActionExecutedContext(
                new ActionContext(context.HttpContext, context.RouteData, context.ActionDescriptor),
                [], null!)
            {
                Result = result
            }));

        return stored;
    }

    /// <summary>Builds the resource-stage context, which runs before model binding.</summary>
    private static ResourceExecutingContext CreateResourceContext(string? idempotencyKey, Stream body)
    {
        var services = new ServiceCollection();
        services.AddSingleton(new Mock<ICacheService>().Object);

        var httpContext = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        httpContext.Request.Method = "POST";
        httpContext.Request.Body = body;
        if (idempotencyKey is not null)
            httpContext.Request.Headers[IdempotencyFilter.IdempotencyKeyHeader] = idempotencyKey;

        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        return new ResourceExecutingContext(actionContext, [], []);
    }

    /// <summary>
    /// A request body as the server first sees it: forward-only. Buffering is what makes it
    /// seekable, so this is the only way to observe whether the filter turned buffering on.
    /// </summary>
    private sealed class NonSeekableStream(byte[] content) : Stream
    {
        private readonly MemoryStream _inner = new(content);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => _inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _inner.Dispose();

            base.Dispose(disposing);
        }
    }

    /// <summary>Lock handle that records how many times it was released.</summary>
    private sealed class TrackingHandle : IAsyncDisposable
    {
        public int Disposals { get; private set; }

        public ValueTask DisposeAsync()
        {
            Disposals++;
            return ValueTask.CompletedTask;
        }
    }
}

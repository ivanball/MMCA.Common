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
}

using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MMCA.Common.API.Idempotency;
using MMCA.Common.Application.Interfaces;
using Moq;

namespace MMCA.Common.API.Tests.Idempotency;

/// <summary>
/// Pins the property the idempotency convention gate depends on: a request WITHOUT an
/// <c>Idempotency-Key</c> header passes through the filter untouched. That is what makes attaching
/// <c>[Idempotent]</c> to an existing POST a non-breaking change for every client already calling it,
/// and therefore what makes blanket adoption safe. If these tests ever go red, the convention gate's
/// premise is gone and the gate must be revisited before the filter's semantics are.
/// </summary>
public sealed class IdempotencyFilterPassthroughTests
{
    private static IdempotencyFilter CreateSut() => new(NullLogger<IdempotencyFilter>.Instance);

    private static (ActionExecutingContext Context, Mock<ICacheService> Cache) CreateContext(
        string? idempotencyKey = null)
    {
        var cache = new Mock<ICacheService>(MockBehavior.Strict);
        var services = new ServiceCollection();
        services.AddSingleton(cache.Object);

        var httpContext = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        httpContext.Request.Method = "POST";

        if (idempotencyKey is not null)
        {
            httpContext.Request.Headers[IdempotencyFilter.IdempotencyKeyHeader] = idempotencyKey;
        }

        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        return (new ActionExecutingContext(actionContext, [], new Dictionary<string, object?>(), null!), cache);
    }

    [Fact]
    public async Task ActionStage_WithoutIdempotencyKeyHeader_RunsTheActionAndTouchesNoCache()
    {
        var (context, cache) = CreateContext();
        var executed = false;

        await CreateSut().OnActionExecutionAsync(context, () =>
        {
            executed = true;
            return Task.FromResult(new ActionExecutedContext(context, [], null!));
        });

        executed.Should().BeTrue("an action with no idempotency key must still run");
        context.Result.Should().BeNull("nothing was short-circuited");
        context.HttpContext.Response.Headers.Should().BeEmpty("no replay marker belongs on a non-deduplicated response");

        // MockBehavior.Strict already fails on any call; this states the intent explicitly.
        cache.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ActionStage_WithBlankIdempotencyKeyHeader_RunsTheActionAndTouchesNoCache(string headerValue)
    {
        var (context, cache) = CreateContext(headerValue);
        var executed = false;

        await CreateSut().OnActionExecutionAsync(context, () =>
        {
            executed = true;
            return Task.FromResult(new ActionExecutedContext(context, [], null!));
        });

        executed.Should().BeTrue("a blank key is treated exactly like an absent one");
        context.Result.Should().BeNull();
        cache.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ResourceStage_WithoutIdempotencyKeyHeader_LeavesTheRequestBodyUnbuffered()
    {
        var (context, _) = CreateContext();
        var originalBody = new MemoryStream("{}"u8.ToArray());
        context.HttpContext.Request.Body = originalBody;

        var resourceContext = new ResourceExecutingContext(context, [], []);
        var executed = false;

        await CreateSut().OnResourceExecutionAsync(resourceContext, () =>
        {
            executed = true;
            return Task.FromResult(new ResourceExecutedContext(context, []));
        });

        executed.Should().BeTrue();
        resourceContext.HttpContext.Request.Body.Should().BeSameAs(
            originalBody,
            "EnableBuffering swaps in a buffering stream, and it runs only for requests that actually "
            + "carry a key, so ordinary traffic pays nothing");
    }
}

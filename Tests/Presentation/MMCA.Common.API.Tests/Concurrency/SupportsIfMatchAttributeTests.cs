using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using MMCA.Common.API.Concurrency;
using MMCA.Common.Shared.DTOs;

namespace MMCA.Common.API.Tests.Concurrency;

/// <summary>
/// The conditional-write filter: where the concurrency token comes from, and which status a conflict
/// gets as a result.
/// </summary>
public sealed class SupportsIfMatchAttributeTests
{
    private static readonly byte[] HeaderRowVersion = [0, 0, 0, 0, 0, 0, 7, 209];
    private static readonly byte[] BodyRowVersion = [9, 9, 9, 9];

    /// <summary>A request record shaped exactly like a real one: init-only, as the immutability rules require.</summary>
    private sealed record UpdateThingRequest : IConcurrencyAware
    {
        public byte[]? RowVersion { get; init; }
    }

    private static ActionExecutingContext CreateContext(string? ifMatch, object? argument)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = "PUT";

        if (ifMatch is not null)
        {
            httpContext.Request.Headers[ConcurrencyETag.IfMatchHeaderName] = ifMatch;
        }

        var arguments = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (argument is not null)
        {
            arguments["request"] = argument;
        }

        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        return new ActionExecutingContext(actionContext, [], arguments, null!);
    }

    /// <summary>Runs the filter with an action that produces <paramref name="actionResult"/>.</summary>
    private static async Task<(ActionExecutingContext Context, IActionResult? Result, bool Executed)> RunAsync(
        ActionExecutingContext context,
        IActionResult? actionResult = null)
    {
        var executed = false;
        ActionExecutedContext? executedContext = null;

        await new SupportsIfMatchAttribute().OnActionExecutionAsync(context, () =>
        {
            executed = true;
            executedContext = new ActionExecutedContext(context, [], null!);
            if (actionResult is not null)
            {
                executedContext.Result = actionResult;
            }

            return Task.FromResult(executedContext);
        });

        return (context, executedContext?.Result ?? context.Result, executed);
    }

    private static ObjectResult Conflict() =>
        new(new ProblemDetails { Status = StatusCodes.Status409Conflict, Title = "Operation failed" })
        {
            StatusCode = StatusCodes.Status409Conflict,
        };

    [Fact]
    public async Task WithoutIfMatch_LeavesTheRequestAloneAndKeeps409()
    {
        var request = new UpdateThingRequest();
        var (context, result, executed) = await RunAsync(CreateContext(ifMatch: null, request), Conflict());

        executed.Should().BeTrue();
        request.RowVersion.Should().BeNull("there was no header to take a token from");
        context.HttpContext.Items.Should().NotContainKey(SupportsIfMatchAttribute.HeaderSourcedItemKey);
        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status409Conflict);
    }

    [Fact]
    public async Task WithIfMatch_PopulatesTheTokenAndFlagsTheSource()
    {
        var request = new UpdateThingRequest();
        var context = CreateContext(ConcurrencyETag.Format(HeaderRowVersion), request);

        var (_, _, executed) = await RunAsync(context);

        executed.Should().BeTrue();
        request.RowVersion.Should().Equal(HeaderRowVersion);
        context.HttpContext.Items[SupportsIfMatchAttribute.HeaderSourcedItemKey].Should().Be(true);
    }

    [Fact]
    public async Task WithMalformedIfMatch_Returns400AndNeverRunsTheAction()
    {
        var request = new UpdateThingRequest();
        var context = CreateContext("W/\"not base64!\"", request);

        var (_, result, executed) = await RunAsync(context);

        executed.Should().BeFalse("a precondition the server cannot read must not be silently ignored");
        request.RowVersion.Should().BeNull();

        var objectResult = result.Should().BeOfType<ObjectResult>().Which;
        objectResult.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        objectResult.Value.Should().BeOfType<ProblemDetails>().Which.Title.Should().Be("Invalid If-Match header");
    }

    [Fact]
    public async Task WithWildcardIfMatch_RunsTheActionWithNoToken()
    {
        var request = new UpdateThingRequest();
        var context = CreateContext(ConcurrencyETag.Wildcard, request);

        var (_, result, executed) = await RunAsync(context, Conflict());

        executed.Should().BeTrue();
        request.RowVersion.Should().BeNull("\"*\" states no particular version");
        context.HttpContext.Items.Should().NotContainKey(SupportsIfMatchAttribute.HeaderSourcedItemKey);
        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status409Conflict);
    }

    [Fact]
    public async Task WhenTheBodyAlreadyCarriesAToken_TheHeaderDoesNotOverwriteItAnd409Stands()
    {
        var request = new UpdateThingRequest { RowVersion = BodyRowVersion };
        var context = CreateContext(ConcurrencyETag.Format(HeaderRowVersion), request);

        var (_, result, _) = await RunAsync(context, Conflict());

        request.RowVersion.Should().Equal(BodyRowVersion, "the body wins; the header only fills a gap");
        context.HttpContext.Items.Should().NotContainKey(SupportsIfMatchAttribute.HeaderSourcedItemKey);
        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(
            StatusCodes.Status409Conflict,
            "a body-sourced token keeps the pre-existing conflict semantics untouched");
    }

    [Fact]
    public async Task WhenHeaderSourced_AConflictBecomes412WithTheSameBody()
    {
        var context = CreateContext(ConcurrencyETag.Format(HeaderRowVersion), new UpdateThingRequest());

        var (_, result, _) = await RunAsync(context, Conflict());

        var objectResult = result.Should().BeOfType<ObjectResult>().Which;
        objectResult.StatusCode.Should().Be(StatusCodes.Status412PreconditionFailed);

        var problemDetails = objectResult.Value.Should().BeOfType<ProblemDetails>().Which;
        problemDetails.Status.Should().Be(StatusCodes.Status412PreconditionFailed);
        problemDetails.Title.Should().Be("Operation failed", "the body shape is preserved, only the status moves");
    }

    [Fact]
    public async Task WhenHeaderSourced_ASuccessIsLeftAlone()
    {
        var context = CreateContext(ConcurrencyETag.Format(HeaderRowVersion), new UpdateThingRequest());

        var (_, result, _) = await RunAsync(context, new NoContentResult());

        result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task WhenHeaderSourced_ABodylessConflictStatusBecomes412()
    {
        var context = CreateContext(ConcurrencyETag.Format(HeaderRowVersion), new UpdateThingRequest());

        var (_, result, _) = await RunAsync(context, new StatusCodeResult(StatusCodes.Status409Conflict));

        result.Should().BeOfType<StatusCodeResult>().Which.StatusCode.Should().Be(StatusCodes.Status412PreconditionFailed);
    }

    [Fact]
    public async Task WhenHeaderSourced_TheEfConcurrencyExceptionIsAnsweredAs412()
    {
        var context = CreateContext(ConcurrencyETag.Format(HeaderRowVersion), new UpdateThingRequest());

        var executedContext = await RunFilterAsync(context, new DbUpdateConcurrencyException("stale"));

        executedContext.ExceptionHandled.Should().BeTrue("the global 409 handler must not also answer this");
        var objectResult = executedContext.Result.Should().BeOfType<ObjectResult>().Which;
        objectResult.StatusCode.Should().Be(StatusCodes.Status412PreconditionFailed);
    }

    [Fact]
    public async Task WhenBodySourced_TheEfConcurrencyExceptionIsLeftToTheGlobalHandler()
    {
        var request = new UpdateThingRequest { RowVersion = BodyRowVersion };
        var context = CreateContext(ifMatch: null, request);

        var executedContext = await RunFilterAsync(context, new DbUpdateConcurrencyException("stale"));

        executedContext.ExceptionHandled.Should().BeFalse();
        executedContext.Exception.Should().BeOfType<DbUpdateConcurrencyException>();
    }

    /// <summary>Runs the filter over an action that threw, returning the executed context itself.</summary>
    private static async Task<ActionExecutedContext> RunFilterAsync(
        ActionExecutingContext context,
        Exception actionException)
    {
        ActionExecutedContext? executedContext = null;

        await new SupportsIfMatchAttribute().OnActionExecutionAsync(context, () =>
        {
            executedContext = new ActionExecutedContext(context, [], null!) { Exception = actionException };
            return Task.FromResult(executedContext);
        });

        executedContext.Should().NotBeNull();
        return executedContext!;
    }
}

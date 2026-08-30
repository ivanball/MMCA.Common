using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using MMCA.Common.API.Concurrency;
using MMCA.Common.Shared.Http;

namespace MMCA.Common.API.Tests.Concurrency;

/// <summary>
/// The conditional-write filter: the <c>If-Match</c> header is the one source of the concurrency
/// token, it is mandatory, and a conflict is answered as a failed precondition.
/// </summary>
public sealed class SupportsIfMatchAttributeTests
{
    private static readonly byte[] HeaderRowVersion = [0, 0, 0, 0, 0, 0, 7, 209];

    private static ActionExecutingContext CreateContext(string? ifMatch)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = "PUT";

        if (ifMatch is not null)
        {
            httpContext.Request.Headers[ConcurrencyETag.IfMatchHeaderName] = ifMatch;
        }

        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        return new ActionExecutingContext(actionContext, [], new Dictionary<string, object?>(StringComparer.Ordinal), null!);
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
    public async Task WithoutIfMatch_Returns428AndNeverRunsTheAction()
    {
        var (context, result, executed) = await RunAsync(CreateContext(ifMatch: null), Conflict());

        executed.Should().BeFalse("a conditional write with no precondition would be last-write-wins");
        context.HttpContext.Items.Should().NotContainKey(SupportsIfMatchAttribute.TokenItemKey);

        var objectResult = result.Should().BeOfType<ObjectResult>().Which;
        objectResult.StatusCode.Should().Be(StatusCodes.Status428PreconditionRequired);
        var problemDetails = objectResult.Value.Should().BeOfType<ProblemDetails>().Which;
        problemDetails.Title.Should().Be("If-Match header required");
        problemDetails.Extensions.Should().ContainKey(
            "errors",
            because: "the problem-details contract carries every failure in the errors extension");
    }

    [Fact]
    public async Task WithBlankIfMatch_Returns428()
    {
        var (_, result, executed) = await RunAsync(CreateContext("   "));

        executed.Should().BeFalse();
        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status428PreconditionRequired);
    }

    [Fact]
    public async Task WithWildcardIfMatch_Returns428()
    {
        var (_, result, executed) = await RunAsync(CreateContext(ConcurrencyETag.Wildcard));

        executed.Should().BeFalse("\"*\" states no particular version, so it is no precondition at all");
        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status428PreconditionRequired);
    }

    [Fact]
    public async Task WithIfMatch_PublishesTheDecodedTokenForTheAction()
    {
        var context = CreateContext(ConcurrencyETag.Format(HeaderRowVersion));

        var (_, _, executed) = await RunAsync(context);

        executed.Should().BeTrue();
        SupportsIfMatchAttribute.RequiredToken(context.HttpContext).Should().Equal(HeaderRowVersion);
    }

    [Fact]
    public async Task WithMalformedIfMatch_Returns400AndNeverRunsTheAction()
    {
        var context = CreateContext("W/\"not base64!\"");

        var (_, result, executed) = await RunAsync(context);

        executed.Should().BeFalse("a precondition the server cannot read must not be silently ignored");
        context.HttpContext.Items.Should().NotContainKey(SupportsIfMatchAttribute.TokenItemKey);

        var objectResult = result.Should().BeOfType<ObjectResult>().Which;
        objectResult.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        objectResult.Value.Should().BeOfType<ProblemDetails>().Which.Title.Should().Be("Invalid If-Match header");
    }

    [Fact]
    public void RequiredToken_WithoutTheFilter_ThrowsRatherThanGuessing()
    {
        var act = () => SupportsIfMatchAttribute.RequiredToken(new DefaultHttpContext());

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*SupportsIfMatchAttribute*", because: "reaching the action without the filter is a wiring mistake");
    }

    [Fact]
    public async Task AConflictBecomes412WithTheSameBody()
    {
        var context = CreateContext(ConcurrencyETag.Format(HeaderRowVersion));

        var (_, result, _) = await RunAsync(context, Conflict());

        var objectResult = result.Should().BeOfType<ObjectResult>().Which;
        objectResult.StatusCode.Should().Be(StatusCodes.Status412PreconditionFailed);

        var problemDetails = objectResult.Value.Should().BeOfType<ProblemDetails>().Which;
        problemDetails.Status.Should().Be(StatusCodes.Status412PreconditionFailed);
        problemDetails.Title.Should().Be("Operation failed", "the body shape is preserved, only the status moves");
    }

    [Fact]
    public async Task ASuccessIsLeftAlone()
    {
        var context = CreateContext(ConcurrencyETag.Format(HeaderRowVersion));

        var (_, result, _) = await RunAsync(context, new NoContentResult());

        result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task ABodylessConflictStatusBecomes412()
    {
        var context = CreateContext(ConcurrencyETag.Format(HeaderRowVersion));

        var (_, result, _) = await RunAsync(context, new StatusCodeResult(StatusCodes.Status409Conflict));

        result.Should().BeOfType<StatusCodeResult>().Which.StatusCode.Should().Be(StatusCodes.Status412PreconditionFailed);
    }

    [Fact]
    public async Task TheEfConcurrencyExceptionIsAnsweredAs412()
    {
        var context = CreateContext(ConcurrencyETag.Format(HeaderRowVersion));

        var executedContext = await RunFilterAsync(context, new DbUpdateConcurrencyException("stale"));

        executedContext.ExceptionHandled.Should().BeTrue("the global 409 handler must not also answer this");
        var objectResult = executedContext.Result.Should().BeOfType<ObjectResult>().Which;
        objectResult.StatusCode.Should().Be(StatusCodes.Status412PreconditionFailed);

        var problemDetails = objectResult.Value.Should().BeOfType<ProblemDetails>().Which;
        problemDetails.Extensions.Should().ContainKey(
            "errors",
            because: "a filter-built 412 must satisfy the same problem-details contract as a handler-built conflict");
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

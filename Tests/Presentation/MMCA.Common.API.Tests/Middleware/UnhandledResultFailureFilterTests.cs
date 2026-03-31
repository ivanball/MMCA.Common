using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using MMCA.Common.API.Middleware;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.API.Tests.Middleware;

public sealed class UnhandledResultFailureFilterTests
{
    // ── Failed Result converted to ProblemDetails ──
    [Fact]
    public void OnResultExecuting_FailedResult_ConvertsToProblemDetails()
    {
        var sut = new UnhandledResultFailureFilter(
            NullLogger<UnhandledResultFailureFilter>.Instance);

        var failedResult = Result.Failure(Error.NotFound);
        var context = CreateContext(new ObjectResult(failedResult));

        sut.OnResultExecuting(context);

        context.Result.Should().BeOfType<ObjectResult>();
        var objectResult = (ObjectResult)context.Result;
        objectResult.Value.Should().BeOfType<ProblemDetails>();
        objectResult.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    // ── Validation error maps to 400 ──
    [Fact]
    public void OnResultExecuting_ValidationError_Returns400()
    {
        var sut = new UnhandledResultFailureFilter(
            NullLogger<UnhandledResultFailureFilter>.Instance);

        var failedResult = Result.Failure(Error.Validation("test", "invalid"));
        var context = CreateContext(new ObjectResult(failedResult));

        sut.OnResultExecuting(context);

        var objectResult = (ObjectResult)context.Result;
        objectResult.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    // ── Conflict error maps to 409 ──
    [Fact]
    public void OnResultExecuting_ConflictError_Returns409()
    {
        var sut = new UnhandledResultFailureFilter(
            NullLogger<UnhandledResultFailureFilter>.Instance);

        var failedResult = Result.Failure(Error.Conflict("test", "conflict"));
        var context = CreateContext(new ObjectResult(failedResult));

        sut.OnResultExecuting(context);

        var objectResult = (ObjectResult)context.Result;
        objectResult.StatusCode.Should().Be(StatusCodes.Status409Conflict);
    }

    // ── Success Result passes through ──
    [Fact]
    public void OnResultExecuting_SuccessResult_DoesNotModifyResult()
    {
        var sut = new UnhandledResultFailureFilter(
            NullLogger<UnhandledResultFailureFilter>.Instance);

        var successResult = Result.Success();
        var originalResult = new ObjectResult(successResult);
        var context = CreateContext(originalResult);

        sut.OnResultExecuting(context);

        context.Result.Should().Be(originalResult);
    }

    // ── Non-Result value passes through ──
    [Fact]
    public void OnResultExecuting_NonResultValue_DoesNotModifyResult()
    {
        var sut = new UnhandledResultFailureFilter(
            NullLogger<UnhandledResultFailureFilter>.Instance);

        var originalResult = new ObjectResult("just a string");
        var context = CreateContext(originalResult);

        sut.OnResultExecuting(context);

        context.Result.Should().Be(originalResult);
    }

    // ── OnResultExecuted does nothing ──
    [Fact]
    public void OnResultExecuted_DoesNotThrow()
    {
        var sut = new UnhandledResultFailureFilter(
            NullLogger<UnhandledResultFailureFilter>.Instance);

        var httpContext = new DefaultHttpContext();
        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        var context = new ResultExecutedContext(actionContext, [], new ObjectResult(null), null!);

        FluentActions.Invoking(() => sut.OnResultExecuted(context))
            .Should().NotThrow();
    }

    // ── Helper ──
    private static ResultExecutingContext CreateContext(IActionResult result)
    {
        var httpContext = new DefaultHttpContext();
        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        return new ResultExecutingContext(actionContext, [], result, null!);
    }
}

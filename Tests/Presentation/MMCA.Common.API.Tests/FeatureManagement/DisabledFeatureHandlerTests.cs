using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using MMCA.Common.API.FeatureManagement;

namespace MMCA.Common.API.Tests.FeatureManagement;

public sealed class DisabledFeatureHandlerTests
{
    private static ActionExecutingContext CreateContext()
    {
        var httpContext = new DefaultHttpContext();
        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        return new ActionExecutingContext(actionContext, [], new Dictionary<string, object?>(), null!);
    }

    [Fact]
    public async Task HandleDisabledFeatures_SetsResult_ToObjectResult()
    {
        var sut = new DisabledFeatureHandler();
        ActionExecutingContext context = CreateContext();

        await sut.HandleDisabledFeatures(["SomeFeature"], context);

        context.Result.Should().BeOfType<ObjectResult>();
    }

    [Fact]
    public async Task HandleDisabledFeatures_Returns404StatusCode()
    {
        var sut = new DisabledFeatureHandler();
        ActionExecutingContext context = CreateContext();

        await sut.HandleDisabledFeatures(["SomeFeature"], context);

        var objectResult = context.Result as ObjectResult;
        objectResult.Should().NotBeNull();
        objectResult!.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task HandleDisabledFeatures_SetsProblemDetailsTitleAndDetail()
    {
        var sut = new DisabledFeatureHandler();
        ActionExecutingContext context = CreateContext();

        await sut.HandleDisabledFeatures(["SomeFeature"], context);

        var objectResult = context.Result as ObjectResult;
        var problemDetails = objectResult?.Value as ProblemDetails;
        problemDetails.Should().NotBeNull();
        problemDetails!.Status.Should().Be(StatusCodes.Status404NotFound);
        problemDetails.Title.Should().Be("Feature not available");
        problemDetails.Detail.Should().Be("The requested feature is not currently available.");
    }

    [Fact]
    public async Task HandleDisabledFeatures_ReturnsCompletedTask()
    {
        var sut = new DisabledFeatureHandler();
        ActionExecutingContext context = CreateContext();

        Task result = sut.HandleDisabledFeatures(["SomeFeature"], context);

        result.IsCompleted.Should().BeTrue();
        await result;
    }
}

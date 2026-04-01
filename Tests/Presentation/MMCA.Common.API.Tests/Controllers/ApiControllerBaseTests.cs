using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using MMCA.Common.API.Controllers;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.API.Tests.Controllers;

public sealed class ApiControllerBaseTests
{
    private static TestApiController CreateController() =>
        new()
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };

    [Fact]
    public void HandleFailure_WithValidationError_Returns400()
    {
        TestApiController sut = CreateController();
        Error[] errors = [Error.Validation("Test.Validation", "Validation failed")];

        ObjectResult result = sut.InvokeHandleFailure(errors);

        result.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public void HandleFailure_WithInvariantError_Returns400()
    {
        TestApiController sut = CreateController();
        Error[] errors = [Error.Invariant("Test.Invariant", "Invariant violated")];

        ObjectResult result = sut.InvokeHandleFailure(errors);

        result.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public void HandleFailure_WithNotFoundError_Returns404()
    {
        TestApiController sut = CreateController();
        Error[] errors = [Error.NotFoundError("Test.NotFound", "Entity not found")];

        ObjectResult result = sut.InvokeHandleFailure(errors);

        result.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public void HandleFailure_WithConflictError_Returns409()
    {
        TestApiController sut = CreateController();
        Error[] errors = [Error.Conflict("Test.Conflict", "Conflict detected")];

        ObjectResult result = sut.InvokeHandleFailure(errors);

        result.StatusCode.Should().Be(StatusCodes.Status409Conflict);
    }

    [Fact]
    public void HandleFailure_WithUnauthorizedError_Returns401()
    {
        TestApiController sut = CreateController();
        Error[] errors = [Error.Unauthorized("Test.Unauthorized", "Not authenticated")];

        ObjectResult result = sut.InvokeHandleFailure(errors);

        result.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public void HandleFailure_WithForbiddenError_Returns403()
    {
        TestApiController sut = CreateController();
        Error[] errors = [Error.Forbidden("Test.Forbidden", "Access denied")];

        ObjectResult result = sut.InvokeHandleFailure(errors);

        result.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public void HandleFailure_WithUnprocessableEntityError_Returns422()
    {
        TestApiController sut = CreateController();
        Error[] errors = [Error.UnprocessableEntity("Test.Unprocessable", "Cannot process")];

        ObjectResult result = sut.InvokeHandleFailure(errors);

        result.StatusCode.Should().Be(StatusCodes.Status422UnprocessableEntity);
    }

    [Fact]
    public void HandleFailure_WithFailureError_Returns400()
    {
        TestApiController sut = CreateController();
        Error[] errors = [Error.Failure("Test.Failure", "General failure")];

        ObjectResult result = sut.InvokeHandleFailure(errors);

        result.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public void HandleFailure_WithNullErrors_Returns500()
    {
        TestApiController sut = CreateController();

        ObjectResult result = sut.InvokeHandleFailure(null!);

        result.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
        var problemDetails = result.Value as ProblemDetails;
        problemDetails.Should().NotBeNull();
        problemDetails!.Title.Should().Be("Unknown error");
    }

    [Fact]
    public void HandleFailure_WithEmptyErrors_Returns500()
    {
        TestApiController sut = CreateController();

        ObjectResult result = sut.InvokeHandleFailure([]);

        result.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
        var problemDetails = result.Value as ProblemDetails;
        problemDetails.Should().NotBeNull();
        problemDetails!.Title.Should().Be("Unknown error");
    }

    [Fact]
    public void HandleFailure_WithMultipleErrors_UsesFirstErrorTypeForStatusCode()
    {
        TestApiController sut = CreateController();
        Error[] errors =
        [
            Error.NotFoundError("Test.NotFound", "Entity not found"),
            Error.Validation("Test.Validation", "Validation failed"),
            Error.Conflict("Test.Conflict", "Conflict detected")
        ];

        ObjectResult result = sut.InvokeHandleFailure(errors);

        result.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public void HandleFailure_IncludesErrorDetailsInExtensions()
    {
        TestApiController sut = CreateController();
        Error[] errors = [Error.Validation("Field.Required", "Name is required", "CreateOrder", "Name")];

        ObjectResult result = sut.InvokeHandleFailure(errors);

        var problemDetails = result.Value as ProblemDetails;
        problemDetails.Should().NotBeNull();
        problemDetails!.Title.Should().Be("Operation failed");
        problemDetails.Detail.Should().Be("One or more errors occurred.");
        problemDetails.Extensions.Should().ContainKey("errors");

        var errorEntries = problemDetails.Extensions["errors"] as object[];
        errorEntries.Should().NotBeNull();
        errorEntries!.Should().HaveCount(1);

        // Anonymous types are internal to the declaring assembly, so use reflection
        object entry = errorEntries[0];
        System.Type entryType = entry.GetType();

        entryType.GetProperty("Code")!.GetValue(entry).Should().Be("Field.Required");
        entryType.GetProperty("Message")!.GetValue(entry).Should().Be("Name is required");
        entryType.GetProperty("Type")!.GetValue(entry).Should().Be("Validation");
        entryType.GetProperty("Source")!.GetValue(entry).Should().Be("CreateOrder");
        entryType.GetProperty("Target")!.GetValue(entry).Should().Be("Name");
    }
}

internal sealed class TestApiController : ApiControllerBase
{
    public ObjectResult InvokeHandleFailure(IEnumerable<Error> errors) =>
        HandleFailure(errors);
}

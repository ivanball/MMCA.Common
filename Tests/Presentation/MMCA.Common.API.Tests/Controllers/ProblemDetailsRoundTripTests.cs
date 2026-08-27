using System.Text.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using MMCA.Common.API.Controllers;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Http;

namespace MMCA.Common.API.Tests.Controllers;

/// <summary>
/// Closes the loop between the two halves of the error contract: <c>ApiControllerBase.HandleFailure</c>
/// writes a failed <see cref="Result"/> out as RFC 9457 Problem Details, and
/// <see cref="ProblemDetailsResultReader"/> (in <c>MMCA.Common.Shared</c>, where the UI can reach it)
/// reads it back. These tests emit through the real controller path, serialize with the same
/// options ASP.NET Core MVC uses on the wire, and assert the errors survive the trip.
/// <para>
/// This is the guard that keeps the reader honest: change the emission shape and these fail, not a
/// downstream UI at runtime.
/// </para>
/// </summary>
public sealed class ProblemDetailsRoundTripTests
{
    public static TheoryData<ErrorType, int> EveryErrorType() => new()
    {
        { ErrorType.Validation, StatusCodes.Status400BadRequest },
        { ErrorType.Invariant, StatusCodes.Status400BadRequest },
        { ErrorType.Failure, StatusCodes.Status400BadRequest },
        { ErrorType.NotFound, StatusCodes.Status404NotFound },
        { ErrorType.Conflict, StatusCodes.Status409Conflict },
        { ErrorType.Unauthorized, StatusCodes.Status401Unauthorized },
        { ErrorType.Forbidden, StatusCodes.Status403Forbidden },
        { ErrorType.UnprocessableEntity, StatusCodes.Status422UnprocessableEntity },
        { ErrorType.Unexpected, StatusCodes.Status500InternalServerError },
    };

    [Theory]
    [MemberData(nameof(EveryErrorType))]
    public void EveryErrorType_SurvivesEmissionAndReadBack(ErrorType errorType, int expectedStatus)
    {
        var original = new Error("Round.Trip", "A message that must survive", errorType, "TheSource", "TheTarget");

        (int statusCode, string json) = Emit([original]);

        statusCode.Should().Be(expectedStatus);

        var readBack = ProblemDetailsResultReader.ParseProblemDetails(statusCode, json);

        readBack.Should().ContainSingle();
        readBack[0].Code.Should().Be(original.Code);
        readBack[0].Message.Should().Be(original.Message);
        readBack[0].Type.Should().Be(
            original.Type,
            "the emitted payload states the error type, so the reader never has to guess it from the status");
        readBack[0].Source.Should().Be(original.Source);
        readBack[0].Target.Should().Be(original.Target);
    }

    [Fact]
    public void AnAggregateFailure_RoundTripsEveryErrorAndKeepsTheRankedStatus()
    {
        var combined = Result.Combine(
            Result.Failure(Error.Validation("Order.Name", "Name is required", "CreateOrder", "Name")),
            Result.Failure(Error.Forbidden("Order.Denied", "Not your order")),
            Result.Failure(Error.NotFoundError("Order.Missing", "No such order")));

        (int statusCode, string json) = Emit(combined.Errors);

        statusCode.Should().Be(StatusCodes.Status403Forbidden, "Forbidden is the most severe type present");

        var readBack = ProblemDetailsResultReader.ParseProblemDetails(statusCode, json);

        readBack.Should().HaveCount(3);
        readBack.Select(e => e.Code).Should().Equal("Order.Name", "Order.Denied", "Order.Missing");
        readBack.Select(e => e.Type).Should().Equal(ErrorType.Validation, ErrorType.Forbidden, ErrorType.NotFound);

        // The read-back list classifies exactly like the emitted one did.
        ErrorTypeSeverity.MostSevere(readBack).Type.Should().Be(ErrorType.Forbidden);
    }

    [Fact]
    public void OmittedSourceAndTarget_RoundTripAsNull()
    {
        (int statusCode, string json) = Emit([Error.Conflict("Order.Duplicate", "Already exists")]);

        var readBack = ProblemDetailsResultReader.ParseProblemDetails(statusCode, json);

        readBack[0].Source.Should().BeNull();
        readBack[0].Target.Should().BeNull();
    }

    [Fact]
    public void TheEmittedPayload_UsesTheMemberNamesTheReaderLooksFor()
    {
        // Pins the wire contract the Shared reader's literals are written against. If MVC's naming
        // policy or the anonymous projection in ErrorHttpMapping changes, this fails first.
        (_, string json) = Emit([Error.Validation("Field.Required", "Name is required", "CreateOrder", "Name")]);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        root.TryGetProperty("status", out _).Should().BeTrue();
        root.TryGetProperty("title", out _).Should().BeTrue();
        root.TryGetProperty("detail", out _).Should().BeTrue();
        root.TryGetProperty("errors", out var errors).Should().BeTrue();
        errors.ValueKind.Should().Be(JsonValueKind.Array);

        var entry = errors[0];
        entry.GetProperty("code").GetString().Should().Be("Field.Required");
        entry.GetProperty("message").GetString().Should().Be("Name is required");
        entry.GetProperty("type").GetString().Should().Be("Validation");
        entry.GetProperty("source").GetString().Should().Be("CreateOrder");
        entry.GetProperty("target").GetString().Should().Be("Name");
    }

    [Fact]
    public void TheUnknownErrorResponse_ReadsBackAsASingleUnexpectedError()
    {
        // HandleFailure answers 500 "Unknown error" for a null or empty error list. That response
        // carries no errors extension, so the reader falls back to the status-derived type.
        (int statusCode, string json) = Emit(null);

        statusCode.Should().Be(StatusCodes.Status500InternalServerError);

        var readBack = ProblemDetailsResultReader.ParseProblemDetails(statusCode, json);

        readBack.Should().ContainSingle();
        readBack[0].Type.Should().Be(ErrorType.Unexpected);
    }

    /// <summary>
    /// Runs the real controller path and serializes the resulting payload with MVC's own default
    /// serializer options, so the JSON under test is the JSON that reaches the wire (camelCase
    /// members, <c>ProblemDetails.Extensions</c> flattened into the object by its
    /// <c>JsonExtensionData</c> attribute). Taking the options from <see cref="JsonOptions"/>
    /// rather than restating them means a framework change to the naming or null policy shows up
    /// here instead of silently breaking the reader.
    /// </summary>
    private static (int StatusCode, string Json) Emit(IEnumerable<Error>? errors)
    {
        var controller = new RoundTripController
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        ObjectResult result = controller.InvokeHandleFailure(errors!);
        var problemDetails = result.Value.Should().BeAssignableTo<ProblemDetails>().Subject;
        JsonSerializerOptions mvcOptions = new JsonOptions().JsonSerializerOptions;

        return (result.StatusCode!.Value, JsonSerializer.Serialize(problemDetails, mvcOptions));
    }

    private sealed class RoundTripController : ApiControllerBase
    {
        public ObjectResult InvokeHandleFailure(IEnumerable<Error> errors) => HandleFailure(errors);
    }
}

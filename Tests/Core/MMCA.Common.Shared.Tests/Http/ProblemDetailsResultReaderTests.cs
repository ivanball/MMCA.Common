using System.Net;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Http;

namespace MMCA.Common.Shared.Tests.Http;

/// <summary>
/// Covers the reverse edge: an RFC 9457 payload emitted by the API turned back into
/// <see cref="Error"/> instances. The payload literals here are the real wire shapes; the API test
/// project holds the round-trip guard that keeps them honest against live emission.
/// </summary>
public sealed class ProblemDetailsResultReaderTests
{
    /// <summary>
    /// The shape emitted by <c>ApiControllerBase.HandleFailure</c>: a ProblemDetails whose
    /// <c>errors</c> extension is an array of code/message/type/source/target objects, camelCased
    /// by the MVC serializer.
    /// </summary>
    private const string MmcaErrorArrayPayload = """{ "title": "Operation failed", "status": 409, "detail": "One or more errors occurred.", "errors": [ { "code": "Order.Duplicate", "message": "That order already exists", "type": "Conflict", "source": "CreateOrder", "target": "OrderNumber" } ] }""";

    [Fact]
    public void ParseProblemDetails_MmcaErrorArray_PreservesEveryField()
    {
        var errors = ProblemDetailsResultReader.ParseProblemDetails(409, MmcaErrorArrayPayload);

        errors.Should().ContainSingle();
        errors[0].Code.Should().Be("Order.Duplicate");
        errors[0].Message.Should().Be("That order already exists");
        errors[0].Type.Should().Be(ErrorType.Conflict);
        errors[0].Source.Should().Be("CreateOrder");
        errors[0].Target.Should().Be("OrderNumber");
    }

    [Fact]
    public void ParseProblemDetails_MmcaErrorArray_KeepsEveryErrorNotJustTheMostSevere()
    {
        const string payload = """{ "title": "Operation failed", "status": 500, "errors": [ { "code": "A.Validation", "message": "bad input", "type": "Validation", "source": null, "target": null }, { "code": "B.Broken", "message": "the server broke", "type": "Unexpected", "source": null, "target": null } ] }""";

        var errors = ProblemDetailsResultReader.ParseProblemDetails(500, payload);

        errors.Should().HaveCount(2);
        errors[0].Type.Should().Be(ErrorType.Validation);
        errors[1].Type.Should().Be(ErrorType.Unexpected);
        ErrorTypeSeverity.MostSevere(errors).Code.Should().Be("B.Broken");
    }

    [Fact]
    public void ParseProblemDetails_NullSourceAndTarget_ComeBackAsNull()
    {
        const string payload = """{ "status": 400, "errors": [ { "code": "Field.Required", "message": "Name is required", "type": "Validation", "source": null, "target": null } ] }""";

        var errors = ProblemDetailsResultReader.ParseProblemDetails(400, payload);

        errors[0].Source.Should().BeNull();
        errors[0].Target.Should().BeNull();
    }

    [Fact]
    public void ParseProblemDetails_ErrorTypePreserved_EvenWhenItDisagreesWithTheStatus()
    {
        // A 400 body whose errors are typed Invariant: the payload wins over the reverse status
        // mapping, which is exactly what makes the array shape lossless.
        const string payload = """{ "status": 400, "errors": [ { "code": "Order.Empty", "message": "no lines", "type": "Invariant" } ] }""";

        var errors = ProblemDetailsResultReader.ParseProblemDetails(400, payload);

        errors[0].Type.Should().Be(ErrorType.Invariant);
    }

    [Fact]
    public void ParseProblemDetails_PascalCasePayload_ParsesToo()
    {
        const string payload = """{ "Status": 404, "Errors": [ { "Code": "Order.NotFound", "Message": "no such order", "Type": "NotFound", "Target": "Id" } ] }""";

        var errors = ProblemDetailsResultReader.ParseProblemDetails(404, payload);

        errors.Should().ContainSingle();
        errors[0].Code.Should().Be("Order.NotFound");
        errors[0].Type.Should().Be(ErrorType.NotFound);
        errors[0].Target.Should().Be("Id");
    }

    [Fact]
    public void ParseProblemDetails_UnknownTypeName_FallsBackToTheStatusDerivedType()
    {
        const string payload = """{ "status": 403, "errors": [ { "code": "Some.Code", "message": "nope", "type": "NotAnErrorType" } ] }""";

        var errors = ProblemDetailsResultReader.ParseProblemDetails(403, payload);

        errors[0].Type.Should().Be(ErrorType.Forbidden);
    }

    [Fact]
    public void ParseProblemDetails_NumericTypeName_IsRejected()
    {
        const string payload = """{ "status": 404, "errors": [ { "code": "Some.Code", "message": "nope", "type": "9999" } ] }""";

        var errors = ProblemDetailsResultReader.ParseProblemDetails(404, payload);

        errors[0].Type.Should().Be(ErrorType.NotFound);
    }

    [Fact]
    public void ParseProblemDetails_MissingTypeMember_FallsBackToTheStatusDerivedType()
    {
        const string payload = """{ "status": 422, "errors": [ { "code": "Order.Immutable", "message": "cannot change that" } ] }""";

        var errors = ProblemDetailsResultReader.ParseProblemDetails(422, payload);

        errors[0].Code.Should().Be("Order.Immutable");
        errors[0].Type.Should().Be(ErrorType.UnprocessableEntity);
    }

    [Fact]
    public void ParseProblemDetails_ValidationDictionary_YieldsOneErrorPerMessage()
    {
        // The shape ValidationExceptionHandler and ASP.NET Core model validation emit.
        const string payload = """{ "title": "Validation Exception", "status": 400, "detail": "One or more validation errors occurred", "errors": { "Name": ["Name is required", "Name is too short"], "Email": ["Email is not valid"] } }""";

        var errors = ProblemDetailsResultReader.ParseProblemDetails(400, payload);

        errors.Should().HaveCount(3);
        errors.Should().AllSatisfy(error => error.Type.Should().Be(ErrorType.Validation));
        errors[0].Code.Should().Be("Validation.Name");
        errors[0].Message.Should().Be("Name is required");
        errors[0].Target.Should().Be("Name");
        errors[2].Code.Should().Be("Validation.Email");
        errors[2].Target.Should().Be("Email");
    }

    [Fact]
    public void ParseProblemDetails_ValidationDictionary_ObjectLevelRuleHasNoTarget()
    {
        const string payload = """{ "status": 400, "errors": { "": ["The request is not coherent"] } }""";

        var errors = ProblemDetailsResultReader.ParseProblemDetails(400, payload);

        errors.Should().ContainSingle();
        errors[0].Code.Should().Be("Validation");
        errors[0].Target.Should().BeNull();
    }

    [Fact]
    public void ParseProblemDetails_PlainProblemDetails_SynthesizesOneErrorFromDetail()
    {
        // The shape DomainExceptionHandler emits: no errors extension at all.
        const string payload = """{ "title": "Domain Exception", "status": 400, "detail": "An order must have at least one line" }""";

        var errors = ProblemDetailsResultReader.ParseProblemDetails(400, payload);

        errors.Should().ContainSingle();
        errors[0].Code.Should().Be("Http.400");
        errors[0].Message.Should().Be("An order must have at least one line");
        errors[0].Type.Should().Be(ErrorType.Validation);
    }

    [Fact]
    public void ParseProblemDetails_PlainProblemDetails_FallsBackToTitleWhenDetailIsAbsent()
    {
        const string payload = """{ "title": "Internal Server Error", "status": 500 }""";

        var errors = ProblemDetailsResultReader.ParseProblemDetails(500, payload);

        errors.Should().ContainSingle();
        errors[0].Message.Should().Be("Internal Server Error");
        errors[0].Type.Should().Be(ErrorType.Unexpected);
    }

    [Fact]
    public void ParseProblemDetails_EmptyErrorsArray_FallsBackToTheSynthesizedError()
    {
        const string payload = """{ "title": "Operation failed", "status": 409, "errors": [] }""";

        var errors = ProblemDetailsResultReader.ParseProblemDetails(409, payload);

        errors.Should().ContainSingle();
        errors[0].Code.Should().Be("Http.409");
        errors[0].Type.Should().Be(ErrorType.Conflict);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("<html><body>Gateway timeout</body></html>")]
    [InlineData("not json at all")]
    [InlineData("[1, 2, 3]")]
    public void ParseProblemDetails_UnreadableBody_YieldsOneStatusCarryingError(string? body)
    {
        var errors = ProblemDetailsResultReader.ParseProblemDetails(401, body);

        errors.Should().ContainSingle();
        errors[0].Code.Should().Be("Http.401");
        errors[0].Type.Should().Be(ErrorType.Unauthorized);
        errors[0].Message.Should().Contain("401");
    }

    [Fact]
    public void ParseProblemDetails_NeverReturnsAnEmptyList()
    {
        // Result.Failure throws on an empty error list, so the reader guaranteeing at least one
        // error is what lets every caller build a failure without a null check.
        ProblemDetailsResultReader.ParseProblemDetails(0, null).Should().NotBeEmpty();
        ProblemDetailsResultReader.ParseProblemDetails(418, "{}").Should().NotBeEmpty();
    }

    [Fact]
    public void ParseProblemDetails_WithNoTransportStatus_FallsBackToTheBodyStatus()
    {
        const string payload = """{ "title": "Operation failed", "status": 404 }""";

        var errors = ProblemDetailsResultReader.ParseProblemDetails(0, payload);

        errors[0].Code.Should().Be("Http.404");
        errors[0].Type.Should().Be(ErrorType.NotFound);
    }

    [Theory]
    [InlineData(500, ErrorType.Unexpected)]
    [InlineData(401, ErrorType.Unauthorized)]
    [InlineData(403, ErrorType.Forbidden)]
    [InlineData(409, ErrorType.Conflict)]
    [InlineData(404, ErrorType.NotFound)]
    [InlineData(422, ErrorType.UnprocessableEntity)]
    [InlineData(400, ErrorType.Validation)]
    [InlineData(429, ErrorType.Failure)]
    [InlineData(405, ErrorType.Failure)]
    [InlineData(503, ErrorType.Unexpected)]
    [InlineData(0, ErrorType.Unexpected)]
    public void FromHttpStatusCode_ReversesTheDocumentedMapping(int statusCode, ErrorType expected) =>
        ProblemDetailsResultReader.FromHttpStatusCode(statusCode).Should().Be(expected);

    [Fact]
    public void ToFailureResult_LiftsTheParsedErrors()
    {
        var result = ProblemDetailsResultReader.ToFailureResult(409, MmcaErrorArrayPayload);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle();
        result.Errors[0].Type.Should().Be(ErrorType.Conflict);
    }

    [Fact]
    public async Task ReadAsync_OnSuccess_ReturnsSuccess()
    {
        using var response = CreateResponse(HttpStatusCode.NoContent, null);

        var result = await ProblemDetailsResultReader.ReadAsync(response, TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task ReadAsync_OnFailure_CarriesTheParsedErrors()
    {
        using var response = CreateResponse(HttpStatusCode.Conflict, MmcaErrorArrayPayload);

        var result = await ProblemDetailsResultReader.ReadAsync(response, TestContext.Current.CancellationToken);

        result.IsFailure.Should().BeTrue();
        result.Errors[0].Code.Should().Be("Order.Duplicate");
        result.Errors[0].Type.Should().Be(ErrorType.Conflict);
    }

    [Fact]
    public async Task ReadAsync_OnABare401_TypesTheErrorUnauthorized()
    {
        using var response = CreateResponse(HttpStatusCode.Unauthorized, null);

        var result = await ProblemDetailsResultReader.ReadAsync(response, TestContext.Current.CancellationToken);

        result.IsFailure.Should().BeTrue();
        result.Errors[0].Type.Should().Be(ErrorType.Unauthorized);
    }

    [Fact]
    public async Task ReadAsyncOfT_OnSuccess_DeserializesTheBody()
    {
        using var response = CreateResponse(HttpStatusCode.OK, """{ "name": "Ada", "count": 3 }""");

        var result = await ProblemDetailsResultReader.ReadAsync<Payload>(
            response,
            cancellationToken: TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Name.Should().Be("Ada");
        result.Value.Count.Should().Be(3);
    }

    [Fact]
    public async Task ReadAsyncOfT_HonorsExplicitSerializerOptions()
    {
        using var response = CreateResponse(HttpStatusCode.OK, """{ "Name": "Ada", "Count": 3 }""");
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = false };

        var result = await ProblemDetailsResultReader.ReadAsync<Payload>(
            response,
            options,
            TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Name.Should().Be("Ada");
    }

    [Fact]
    public async Task ReadAsyncOfT_OnFailure_CarriesTheParsedErrors()
    {
        using var response = CreateResponse(HttpStatusCode.NotFound, """{ "status": 404, "errors": [ { "code": "Order.NotFound", "message": "gone", "type": "NotFound" } ] }""");

        var result = await ProblemDetailsResultReader.ReadAsync<Payload>(
            response,
            cancellationToken: TestContext.Current.CancellationToken);

        result.IsFailure.Should().BeTrue();
        result.Errors[0].Code.Should().Be("Order.NotFound");
        result.Errors[0].Type.Should().Be(ErrorType.NotFound);
    }

    [Fact]
    public async Task ReadAsyncOfT_OnAnEmptySuccessBody_Fails()
    {
        using var response = CreateResponse(HttpStatusCode.NoContent, null);

        var result = await ProblemDetailsResultReader.ReadAsync<Payload>(
            response,
            cancellationToken: TestContext.Current.CancellationToken);

        result.IsFailure.Should().BeTrue();
        result.Errors[0].Code.Should().Be(ProblemDetailsResultReader.EmptyResponseCode);
    }

    [Fact]
    public async Task ReadAsyncOfT_OnAMalformedSuccessBody_Fails()
    {
        using var response = CreateResponse(HttpStatusCode.OK, "{ not json");

        var result = await ProblemDetailsResultReader.ReadAsync<Payload>(
            response,
            cancellationToken: TestContext.Current.CancellationToken);

        result.IsFailure.Should().BeTrue();
        result.Errors[0].Code.Should().Be(ProblemDetailsResultReader.MalformedResponseCode);
        result.Errors[0].Type.Should().Be(ErrorType.Unexpected);
    }

    [Fact]
    public async Task ReadAsync_WithNullResponse_Throws()
    {
        var act = () => ProblemDetailsResultReader.ReadAsync(null!, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    private static HttpResponseMessage CreateResponse(HttpStatusCode statusCode, string? body)
    {
        var response = new HttpResponseMessage(statusCode);
        if (body is not null)
        {
            response.Content = new StringContent(body, Encoding.UTF8, "application/problem+json");
        }

        return response;
    }

    private sealed record Payload(string Name, int Count);
}

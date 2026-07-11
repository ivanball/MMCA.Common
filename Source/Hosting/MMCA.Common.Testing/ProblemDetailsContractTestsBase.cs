using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using Xunit;

namespace MMCA.Common.Testing;

/// <summary>
/// RFC 9457 Problem Details contract guard for a service host (rubric §9). Error responses must be
/// machine-readable problem documents carrying <c>status</c>, <c>title</c>, and diagnostic
/// extensions (an <c>errors</c> list and/or a correlation id), across the two error-shaping paths:
/// ASP.NET Core model validation (400, <c>application/problem+json</c> with <c>type</c>/<c>traceId</c>)
/// and the framework's <c>HandleFailure</c> Result-error mapping (404 not found). Authored once here
/// and re-run as a thin subclass per host: the subclass supplies the app-specific probe requests
/// (which endpoint to hit and how to authenticate). Hosts with a reachable 409-conflict path
/// (stale <c>RowVersion</c>, duplicate registration, duplicate bookmark, ...) add their own conflict
/// test on top, reusing <see cref="AssertProblemDetailsShapeAsync"/>.
/// </summary>
/// <typeparam name="TFixture">The concrete fixture type implementing <see cref="IIntegrationTestFixture"/>.</typeparam>
public abstract class ProblemDetailsContractTestsBase<TFixture> : IntegrationTestBase<TFixture>
    where TFixture : IIntegrationTestFixture
{
    protected ProblemDetailsContractTestsBase(TFixture fixture)
        : base(fixture)
    {
    }

    [Fact]
    public async Task Validation_400_HasProblemDetailsShape()
    {
        using var response = await SendValidationErrorProbeAsync();

        var body = await AssertProblemDetailsShapeAsync(response, HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Contain("problem+json");
        body.TryGetProperty("type", out _).Should().BeTrue("model-validation problem details include a type URI");
        body.TryGetProperty("traceId", out _).Should().BeTrue("model-validation problem details include a traceId");
        body.TryGetProperty("errors", out _).Should().BeTrue("model-validation problem details include the field errors");
    }

    [Fact]
    public async Task NotFound_404_HasProblemDetailsShape()
    {
        using var response = await SendNotFoundProbeAsync();

        await AssertProblemDetailsShapeAsync(response, HttpStatusCode.NotFound);
    }

    /// <summary>
    /// Sends the request that must produce a 400 model-validation problem, authenticating first when
    /// the endpoint requires it (e.g. <c>pageNumber=0</c> against a <c>[Range(1, int.MaxValue)]</c>
    /// paged read).
    /// </summary>
    protected abstract Task<HttpResponseMessage> SendValidationErrorProbeAsync();

    /// <summary>
    /// Sends the request that must produce a 404 <c>HandleFailure</c> problem, authenticating first
    /// when the endpoint requires it (e.g. reading an id that does not exist).
    /// </summary>
    protected abstract Task<HttpResponseMessage> SendNotFoundProbeAsync();

    /// <summary>
    /// Asserts the RFC 9457 problem shape (JSON content type, echoed <c>status</c>, non-empty
    /// <c>title</c>, and an <c>errors</c> list and/or a correlation id) and returns the parsed body
    /// for endpoint-specific follow-up assertions.
    /// </summary>
    protected static async Task<JsonElement> AssertProblemDetailsShapeAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        response.StatusCode.Should().Be(expected);
        response.Content.Headers.ContentType!.MediaType.Should().Contain("json");

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("status").GetInt32().Should().Be((int)expected, "RFC 9457 problem details echo the HTTP status");
        body.GetProperty("title").GetString().Should().NotBeNullOrWhiteSpace("RFC 9457 problem details carry a title");

        var hasDiagnosticExtension =
            body.TryGetProperty("errors", out _) ||
            body.TryGetProperty("traceId", out _) ||
            body.TryGetProperty("requestId", out _);
        hasDiagnosticExtension.Should().BeTrue("the problem body must carry an errors list and/or a correlation id");

        return body;
    }
}

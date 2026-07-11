using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using AwesomeAssertions;
using MMCA.Common.Testing.UI;

namespace MMCA.Common.UI.Tests.Infrastructure;

/// <summary>
/// Covers the shared <see cref="CapturingHttpMessageHandler"/> (Testing.UI): route-registration
/// mode (method + absolute path match, query ignored, last registration wins, 404 default),
/// responder-delegate mode (invoked once per request, consulted only after routes), the
/// fresh-response-per-request guarantee, and the capture semantics every request records
/// (method, URI, path, path + query, Authorization header, body).
/// </summary>
public sealed class CapturingHttpMessageHandlerTests
{
    private static HttpClient CreateClient(CapturingHttpMessageHandler handler) =>
        new(handler, disposeHandler: false) { BaseAddress = new Uri("https://gateway.test/") };

    // ── Route-registration mode ──
    [Fact]
    public async Task RouteMode_UnmatchedRequest_Returns404WithEmptyBody()
    {
        using var handler = new CapturingHttpMessageHandler();
        using var client = CreateClient(handler);

        using var response = await client.GetAsync(new Uri("/unregistered", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task RouteMode_RegisteredRoute_ReturnsCannedJsonSerializedWithWebDefaults()
    {
        using var handler = new CapturingHttpMessageHandler();
        handler.SetResponse(HttpMethod.Get, "/orders/42", HttpStatusCode.OK, new { OrderId = 42, Status = "Paid" });
        using var client = CreateClient(handler);

        using var response = await client.GetAsync(new Uri("/orders/42", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("orderId").GetInt32().Should().Be(42, "web defaults camelCase property names");
        body.RootElement.GetProperty("status").GetString().Should().Be("Paid");
    }

    [Fact]
    public async Task RouteMode_QueryStringIsIgnored_WhenMatchingTheAbsolutePath()
    {
        using var handler = new CapturingHttpMessageHandler();
        handler.SetResponse(HttpMethod.Get, "/orders", HttpStatusCode.OK);
        using var client = CreateClient(handler);

        using var response = await client.GetAsync(new Uri("/orders?page=2&size=10", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task RouteMode_MethodMustMatch_OtherwiseFallsThroughTo404()
    {
        using var handler = new CapturingHttpMessageHandler();
        handler.SetResponse(HttpMethod.Get, "/orders", HttpStatusCode.OK);
        using var client = CreateClient(handler);

        using var response = await client.PostAsync(new Uri("/orders", UriKind.Relative), content: null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task RouteMode_LastRegistrationWins_ForTheSameMethodAndPath()
    {
        using var handler = new CapturingHttpMessageHandler();
        handler.SetResponse(HttpMethod.Get, "/orders/42", HttpStatusCode.OK);
        handler.SetResponse(HttpMethod.Get, "/orders/42", HttpStatusCode.Conflict);
        using var client = CreateClient(handler);

        using var response = await client.GetAsync(new Uri("/orders/42", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task RouteMode_RawJsonStringBody_PassesThroughVerbatim()
    {
        const string rawJson = /*lang=json,strict*/ """{"answer":42}""";
        using var handler = new CapturingHttpMessageHandler();
        handler.SetResponse(HttpMethod.Get, "/raw", HttpStatusCode.OK, rawJson);
        using var client = CreateClient(handler);

        using var response = await client.GetAsync(new Uri("/raw", UriKind.Relative));

        (await response.Content.ReadAsStringAsync()).Should().Be(rawJson);
    }

    [Fact]
    public async Task RouteMode_NullBody_ProducesAnEmptyResponseBody()
    {
        using var handler = new CapturingHttpMessageHandler();
        handler.SetResponse(HttpMethod.Delete, "/orders/42", HttpStatusCode.NoContent);
        using var client = CreateClient(handler);

        using var response = await client.DeleteAsync(new Uri("/orders/42", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await response.Content.ReadAsStringAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task RouteMode_RepeatedCalls_GetAFreshResponsePerRequest()
    {
        // A Polly retry pipeline re-sends the same logical request; a reused HttpContent would
        // already be consumed, so each request must get a newly built response.
        using var handler = new CapturingHttpMessageHandler();
        handler.SetResponse(HttpMethod.Get, "/orders/42", HttpStatusCode.OK, new { OrderId = 42 });
        using var client = CreateClient(handler);

        using var first = await client.GetAsync(new Uri("/orders/42", UriKind.Relative));
        using var second = await client.GetAsync(new Uri("/orders/42", UriKind.Relative));

        (await first.Content.ReadAsStringAsync()).Should().Be(await second.Content.ReadAsStringAsync());
        second.Should().NotBeSameAs(first);
    }

    // ── Responder-delegate mode ──
    [Fact]
    public async Task ResponderMode_AnswersEveryRequest_AndIsInvokedOncePerRequest()
    {
        int invocations = 0;
        using var handler = new CapturingHttpMessageHandler(_ =>
        {
            invocations++;
            return new HttpResponseMessage(HttpStatusCode.Accepted);
        });
        using var client = CreateClient(handler);

        using var first = await client.GetAsync(new Uri("/a", UriKind.Relative));
        using var second = await client.GetAsync(new Uri("/b", UriKind.Relative));

        first.StatusCode.Should().Be(HttpStatusCode.Accepted);
        second.StatusCode.Should().Be(HttpStatusCode.Accepted);
        invocations.Should().Be(2);
    }

    [Fact]
    public async Task RegisteredRoutes_TakePrecedenceOverTheResponder()
    {
        int responderInvocations = 0;
        using var handler = new CapturingHttpMessageHandler(_ =>
        {
            responderInvocations++;
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        });
        handler.SetResponse(HttpMethod.Get, "/routed", HttpStatusCode.OK);
        using var client = CreateClient(handler);

        using var routed = await client.GetAsync(new Uri("/routed", UriKind.Relative));
        using var fallthrough = await client.GetAsync(new Uri("/unrouted", UriKind.Relative));

        routed.StatusCode.Should().Be(HttpStatusCode.OK);
        fallthrough.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        responderInvocations.Should().Be(1, "the responder must only see requests no route matched");
    }

    // ── Capture semantics ──
    [Fact]
    public async Task EveryRequest_IsCapturedInOrder_WithMethodUriPathQueryAuthorizationAndBody()
    {
        using var handler = new CapturingHttpMessageHandler();
        handler.SetResponse(HttpMethod.Post, "/orders", HttpStatusCode.Created);
        using var client = CreateClient(handler);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "abc123");

        using var content = new StringContent(/*lang=json,strict*/ """{"productId":7}""");
        using var post = await client.PostAsync(new Uri("/orders?source=cart", UriKind.Relative), content);
        using var get = await client.GetAsync(new Uri("/orders", UriKind.Relative));

        handler.Requests.Should().HaveCount(2);

        var captured = handler.Requests[0];
        captured.Method.Should().Be(HttpMethod.Post);
        captured.Uri.Should().Be(new Uri("https://gateway.test/orders?source=cart"));
        captured.Path.Should().Be("/orders");
        captured.PathAndQuery.Should().Be("/orders?source=cart");
        captured.Authorization.Should().Be("Bearer abc123");
        captured.Body.Should().Be(/*lang=json,strict*/ """{"productId":7}""");

        handler.Requests[1].Method.Should().Be(HttpMethod.Get);
        handler.Requests[1].Body.Should().BeNull("the GET request had no content");
    }

    [Fact]
    public async Task RequestsFor_FiltersByMethodAndPath_CaseInsensitively()
    {
        using var handler = new CapturingHttpMessageHandler();
        handler.SetResponse(HttpMethod.Get, "/orders/42", HttpStatusCode.OK);
        handler.SetResponse(HttpMethod.Delete, "/orders/42", HttpStatusCode.NoContent);
        using var client = CreateClient(handler);

        using var first = await client.GetAsync(new Uri("/orders/42", UriKind.Relative));
        using var second = await client.DeleteAsync(new Uri("/orders/42", UriKind.Relative));
        using var third = await client.GetAsync(new Uri("/orders/42", UriKind.Relative));

        handler.RequestsFor(HttpMethod.Get, "/Orders/42").Should().HaveCount(2);
        handler.RequestsFor(HttpMethod.Delete, "/orders/42").Should().ContainSingle();
        handler.RequestsFor(HttpMethod.Put, "/orders/42").Should().BeEmpty();
    }
}

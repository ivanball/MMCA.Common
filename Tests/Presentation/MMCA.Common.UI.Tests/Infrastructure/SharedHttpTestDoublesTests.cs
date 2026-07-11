using System.Net;
using System.Text.Json;
using AwesomeAssertions;
using MMCA.Common.Testing.UI;

namespace MMCA.Common.UI.Tests.Infrastructure;

/// <summary>
/// Covers the shared <see cref="HttpTestDoubles"/> factory helpers (Testing.UI) used by tests that
/// wire the pieces individually instead of through <see cref="UiHttpServiceHarness"/>: the
/// fresh-client factory, the token storage stub, and the canned JSON / empty / ProblemDetails
/// response builders. (Named Shared* to avoid clashing with this project's older test-local
/// HttpTestDoubles file.)
/// </summary>
public sealed class SharedHttpTestDoublesTests
{
    // ── ClientFactory ──
    [Fact]
    public void ClientFactory_UsesTheSharedDefaultBaseAddress_AndHandsOutFreshClients()
    {
        using var handler = new CapturingHttpMessageHandler();
        var factory = HttpTestDoubles.ClientFactory(handler);

        using var first = factory.CreateClient("APIClient");
        using var second = factory.CreateClient("APIClient");

        first.BaseAddress.Should().Be(HttpTestDoubles.BaseAddress);
        HttpTestDoubles.BaseAddress.Should().Be(UiHttpServiceHarness.DefaultBaseAddress);
        second.Should().NotBeSameAs(first);
    }

    [Fact]
    public void ClientFactory_AppliesACustomBaseAddressWhenGiven()
    {
        using var handler = new CapturingHttpMessageHandler();
        var custom = new Uri("https://api.example.test/");

        using var client = HttpTestDoubles.ClientFactory(handler, custom).CreateClient("APIClient");

        client.BaseAddress.Should().Be(custom);
    }

    // ── TokenStorage ──
    [Fact]
    public async Task TokenStorage_ReturnsTheGivenToken_OrNullForAnonymous()
    {
        (await HttpTestDoubles.TokenStorage().GetAccessTokenAsync()).Should().Be("test-token");
        (await HttpTestDoubles.TokenStorage("custom-token").GetAccessTokenAsync()).Should().Be("custom-token");
        (await HttpTestDoubles.TokenStorage(accessToken: null).GetAccessTokenAsync()).Should().BeNull();
    }

    // ── Response builders ──
    [Fact]
    public async Task JsonResponse_SerializesThePayloadWithWebDefaults_AndDefaultsTo200()
    {
        using var response = HttpTestDoubles.JsonResponse(new { OrderId = 42 });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("orderId").GetInt32().Should().Be(42, "web defaults camelCase property names");
    }

    [Fact]
    public void JsonResponse_HonorsAnExplicitStatusCode()
    {
        using var response = HttpTestDoubles.JsonResponse(new { Id = 1 }, HttpStatusCode.Created);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task EmptyResponse_DefaultsTo204NoContent_AndCarriesNoBody()
    {
        using var defaulted = HttpTestDoubles.EmptyResponse();
        using var custom = HttpTestDoubles.EmptyResponse(HttpStatusCode.Unauthorized);

        defaulted.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await defaulted.Content.ReadAsStringAsync()).Should().BeEmpty();
        custom.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ProblemResponse_MatchesTheWebApiDomainFailureShape()
    {
        using var response = HttpTestDoubles.ProblemResponse("Order was already checked out.");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("title").GetString().Should().Be("Domain Exception");
        body.RootElement.GetProperty("detail").GetString().Should().Be("Order was already checked out.");
    }

    [Fact]
    public async Task ProblemResponse_HonorsACustomTitleAndStatusCode()
    {
        using var response = HttpTestDoubles.ProblemResponse("Missing.", "Not Found", HttpStatusCode.NotFound);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("title").GetString().Should().Be("Not Found");
    }
}

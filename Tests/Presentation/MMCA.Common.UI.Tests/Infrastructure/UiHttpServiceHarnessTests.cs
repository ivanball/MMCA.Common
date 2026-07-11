using System.Net;
using AwesomeAssertions;
using MMCA.Common.Testing.UI;

namespace MMCA.Common.UI.Tests.Infrastructure;

/// <summary>
/// Covers <see cref="UiHttpServiceHarness"/> (Testing.UI): the route-registration and
/// responder-delegate construction modes, the default/custom base address and bearer token, and
/// the load-bearing fresh-client-per-call factory contract (services dispose each client after
/// use, so a cached instance would break the second call).
/// </summary>
public sealed class UiHttpServiceHarnessTests
{
    // ── Construction modes ──
    [Fact]
    public async Task RouteMode_UnmatchedRequestsReturn404_AndRegisteredRoutesAnswer()
    {
        using var harness = new UiHttpServiceHarness();
        harness.Handler.SetResponse(HttpMethod.Get, "/orders", HttpStatusCode.OK);
        using var client = harness.ClientFactory.CreateClient("APIClient");

        using var matched = await client.GetAsync(new Uri("/orders", UriKind.Relative));
        using var unmatched = await client.GetAsync(new Uri("/nothing-registered", UriKind.Relative));

        matched.StatusCode.Should().Be(HttpStatusCode.OK);
        unmatched.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ResponderMode_TheDelegateAnswersEveryRequest()
    {
        using var harness = new UiHttpServiceHarness(_ => new HttpResponseMessage(HttpStatusCode.Accepted));
        using var client = harness.ClientFactory.CreateClient("APIClient");

        using var response = await client.GetAsync(new Uri("/anything", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    // ── Base address ──
    [Fact]
    public void DefaultBaseAddress_IsTheSharedGatewayTestOrigin()
    {
        using var harness = new UiHttpServiceHarness();
        using var client = harness.ClientFactory.CreateClient("APIClient");

        harness.BaseAddress.Should().Be(UiHttpServiceHarness.DefaultBaseAddress);
        client.BaseAddress.Should().Be(new Uri("https://gateway.test/"));
    }

    [Fact]
    public void CustomBaseAddress_IsAppliedToEveryCreatedClient()
    {
        var custom = new Uri("https://api.example.test/");
        using var harness = new UiHttpServiceHarness(accessToken: "test-token", baseAddress: custom);
        using var client = harness.ClientFactory.CreateClient("APIClient");

        harness.BaseAddress.Should().Be(custom);
        client.BaseAddress.Should().Be(custom);
    }

    // ── Token storage ──
    [Fact]
    public async Task DefaultToken_IsReturnedByTheStub_AndNullMeansAnonymous()
    {
        using var withToken = new UiHttpServiceHarness();
        using var anonymous = new UiHttpServiceHarness(accessToken: null);

        (await withToken.TokenStorage.GetAccessTokenAsync()).Should().Be("test-token");
        (await anonymous.TokenStorage.GetAccessTokenAsync()).Should().BeNull();
    }

    // ── Fresh-client-per-call factory ──
    [Fact]
    public async Task ClientFactory_HandsOutAFreshClientPerCall_SoDisposingOneNeverBreaksTheNext()
    {
        using var harness = new UiHttpServiceHarness(_ => new HttpResponseMessage(HttpStatusCode.OK));

        var first = harness.ClientFactory.CreateClient("APIClient");
        using var firstResponse = await first.GetAsync(new Uri("/one", UriKind.Relative));
        first.Dispose();

        using var second = harness.ClientFactory.CreateClient("APIClient");
        second.Should().NotBeSameAs(first, "the factory must never cache: services dispose each client after use");

        using var secondResponse = await second.GetAsync(new Uri("/two", UriKind.Relative));
        secondResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        harness.Handler.Requests.Should().HaveCount(2, "the shared handler outlives each disposed client");
    }
}

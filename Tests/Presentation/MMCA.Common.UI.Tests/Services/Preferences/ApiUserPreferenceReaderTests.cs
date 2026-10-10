using System.IdentityModel.Tokens.Jwt;
using System.Net;
using AwesomeAssertions;
using MMCA.Common.UI.Services.Auth.Tokens;
using MMCA.Common.UI.Services.Preferences;
using MMCA.Common.UI.Tests.Infrastructure;
using Moq;

namespace MMCA.Common.UI.Tests.Services.Preferences;

/// <summary>
/// The preference read runs at login reconciliation. In Blazor Server the named <c>"APIClient"</c>'s
/// <c>AuthDelegatingHandler</c> resolves in a separate DI scope whose token store is empty, so the
/// reader cannot rely on that handler for the bearer: it must attach the token it already read
/// (the same scope problem <c>AuthenticatedServiceBase.CreateAuthenticatedClientAsync</c> documents).
/// The factory here hands out a client with NO auth handler, which is exactly the Server-mode shape.
/// </summary>
public sealed class ApiUserPreferenceReaderTests
{
    private const string PreferencesJson = """{"culture":"es","theme":"dark"}""";

    private static string Jwt(DateTime expires) =>
        new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            notBefore: expires.AddMinutes(-60),
            expires: expires));

    private static string FreshJwt() => Jwt(DateTime.UtcNow.AddMinutes(30));

    private static string ExpiredJwt() => Jwt(DateTime.UtcNow.AddMinutes(-5));

    private static (ApiUserPreferenceReader Reader, StubHttpMessageHandler Handler) CreateSut(string? token)
    {
        var handler = StubHttpMessageHandler.RespondingWith(HttpStatusCode.OK, PreferencesJson);
        var storage = new Mock<ITokenStorageService>();
        storage.Setup(s => s.GetAccessTokenAsync()).ReturnsAsync(token);
        return (new ApiUserPreferenceReader(new StubHttpClientFactory(handler), storage.Object), handler);
    }

    [Fact]
    public async Task GetAsync_WithFreshToken_SendsTheBearerItself_WhenTheClientHasNoAuthHandler()
    {
        // Defect A-03: today the reader relies on AuthDelegatingHandler, which in Server mode sees an
        // empty token store, so the GET goes out anonymous, answers 401, and preferences come back empty.
        var token = FreshJwt();
        var (reader, handler) = CreateSut(token);

        await reader.GetAsync(TestContext.Current.CancellationToken);

        handler.CallCount.Should().Be(1);
        handler.LastRequest.Method.Should().Be(HttpMethod.Get);
        handler.LastRequest.Uri!.AbsolutePath.Should().EndWith("auth/preferences");
        handler.LastRequest.Authorization.Should().NotBeNull(
            "the reader holds the fresh token and must attach it, not depend on a handler in another DI scope");
        handler.LastRequest.Authorization!.Scheme.Should().Be("Bearer");
        handler.LastRequest.Authorization.Parameter.Should().Be(token);
    }

    [Fact]
    public async Task GetAsync_WhenAnonymous_DoesNotCallTheApi()
    {
        var (reader, handler) = CreateSut(token: null);

        var result = await reader.GetAsync(TestContext.Current.CancellationToken);

        handler.CallCount.Should().Be(0);
        result.Culture.Should().BeNull();
        result.Theme.Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_WithExpiredToken_DoesNotCallTheApi()
    {
        var (reader, handler) = CreateSut(ExpiredJwt());

        await reader.GetAsync(TestContext.Current.CancellationToken);

        handler.CallCount.Should().Be(0, "an expired token cannot be accepted, so the request is pure cost");
    }
}

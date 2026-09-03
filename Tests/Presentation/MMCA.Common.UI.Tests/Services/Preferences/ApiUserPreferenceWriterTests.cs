using System.IdentityModel.Tokens.Jwt;
using System.Net;
using AwesomeAssertions;
using MMCA.Common.UI.Services.Auth.Tokens;
using MMCA.Common.UI.Services.Preferences;
using MMCA.Common.UI.Tests.Infrastructure;
using Moq;

namespace MMCA.Common.UI.Tests.Services.Preferences;

/// <summary>
/// The preference write is best-effort: the caller never learns it failed, so a request that cannot
/// succeed is pure cost and still counts as a failed request in telemetry. These pin the two guards that
/// keep a signed-out or stale session from spending one 401 per theme/culture toggle (ADR-027/028).
/// </summary>
public sealed class ApiUserPreferenceWriterTests
{
    private static string Jwt(DateTime expires) =>
        new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            notBefore: expires.AddMinutes(-60),
            expires: expires));

    private static string FreshJwt() => Jwt(DateTime.UtcNow.AddMinutes(30));

    private static string ExpiredJwt() => Jwt(DateTime.UtcNow.AddMinutes(-5));

    private static (ApiUserPreferenceWriter Writer, StubHttpMessageHandler Handler) CreateSut(
        string? token,
        HttpStatusCode status = HttpStatusCode.NoContent)
    {
        var handler = StubHttpMessageHandler.RespondingWith(status);
        var storage = new Mock<ITokenStorageService>();
        storage.Setup(s => s.GetAccessTokenAsync()).ReturnsAsync(token);
        return (new ApiUserPreferenceWriter(new StubHttpClientFactory(handler), storage.Object), handler);
    }

    [Fact]
    public async Task SaveAsync_WithFreshToken_PutsThePreference()
    {
        var (writer, handler) = CreateSut(FreshJwt());

        await writer.SaveAsync(culture: "es", theme: null);

        handler.CallCount.Should().Be(1);
        handler.LastRequest.Method.Should().Be(HttpMethod.Put);
        handler.LastRequest.Uri!.AbsolutePath.Should().EndWith("auth/preferences");
    }

    [Fact]
    public async Task SaveAsync_WhenAnonymous_DoesNotCallTheApi()
    {
        var (writer, handler) = CreateSut(token: null);

        await writer.SaveAsync(culture: "es", theme: null);

        handler.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task SaveAsync_WithExpiredToken_DoesNotSpendAGuaranteed401()
    {
        var (writer, handler) = CreateSut(ExpiredJwt());

        await writer.SaveAsync(culture: "es", theme: null);

        handler.CallCount.Should().Be(0, "an expired token cannot be accepted, so the request is pure cost");
    }

    [Fact]
    public async Task SaveAsync_WithUnreadableToken_DoesNotCallTheApi()
    {
        var (writer, handler) = CreateSut("not-a-jwt");

        await writer.SaveAsync(culture: "es", theme: null);

        handler.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task SaveAsync_AfterA401_StopsRetryingWithTheSameToken()
    {
        // Unexpired but refused: a revoked session or rotated signing key. Expiry cannot predict this,
        // so only the response tells us, and every later toggle would repeat the same failed request.
        var (writer, handler) = CreateSut(FreshJwt(), HttpStatusCode.Unauthorized);

        await writer.SaveAsync(culture: "es", theme: null);
        await writer.SaveAsync(culture: null, theme: "dark");
        await writer.SaveAsync(culture: "en-US", theme: null);

        handler.CallCount.Should().Be(1, "three toggles on a rejected session must cost one 401, not three");
    }

    [Fact]
    public async Task SaveAsync_AfterA401_ResumesOnceTheUserSignsInAgain()
    {
        var rejected = FreshJwt();
        var handler = StubHttpMessageHandler.RespondingWith(HttpStatusCode.Unauthorized);
        var storage = new Mock<ITokenStorageService>();
        storage.Setup(s => s.GetAccessTokenAsync()).ReturnsAsync(rejected);
        var writer = new ApiUserPreferenceWriter(new StubHttpClientFactory(handler), storage.Object);

        await writer.SaveAsync(culture: "es", theme: null);
        await writer.SaveAsync(culture: "es", theme: null);
        handler.CallCount.Should().Be(1);

        // A new sign-in yields a different token, which the writer must not treat as the rejected one.
        // The expiry has to differ: JWTs carry second-resolution timestamps and no other varying claim
        // here, so two tokens minted in the same second are byte-identical and ARE the same session.
        storage.Setup(s => s.GetAccessTokenAsync()).ReturnsAsync(Jwt(DateTime.UtcNow.AddMinutes(45)));
        await writer.SaveAsync(culture: "es", theme: null);

        handler.CallCount.Should().Be(2, "a new token is a new session and deserves its own attempt");
    }

    [Fact]
    public async Task SaveAsync_AfterASuccess_KeepsWriting()
    {
        var (writer, handler) = CreateSut(FreshJwt());

        await writer.SaveAsync(culture: "es", theme: null);
        await writer.SaveAsync(culture: null, theme: "dark");

        handler.CallCount.Should().Be(2, "nothing was rejected, so nothing should be suppressed");
    }
}

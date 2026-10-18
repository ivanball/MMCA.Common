using AwesomeAssertions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using MMCA.Common.Grpc.Authentication;
using Xunit;

namespace MMCA.Common.Grpc.Tests.Authentication;

/// <summary>
/// Pins what <see cref="ServiceTokenProvider"/> mints and when it re-mints. The token is a compact
/// HS256 JWT that the receiving service validates with the shared key, the configured issuer and
/// audience, and HS256 only; its subject and <c>svc</c> claim name the calling service; its lifetime
/// is exactly <see cref="ServiceTokenSettings.LifetimeMinutes"/>; and one token is reused until its
/// last 60 seconds, so a burst of calls does not mint a token per call.
/// </summary>
public sealed class ServiceTokenProviderTests
{
    private const int LifetimeMinutes = 5;

    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(LifetimeMinutes);

    [Fact]
    public async Task GetToken_ReturnsATokenThatValidatesWithTheSharedKeyIssuerAudienceAndHs256()
    {
        var key = ServiceTokenTestData.NewBase64Key();
        var sut = CreateSut(key, new FakeTimeProvider(WholeSecondNow()));

        var token = sut.GetToken();

        var result = await new JsonWebTokenHandler().ValidateTokenAsync(token, ServiceTokenTestData.ValidationParameters(key));
        result.IsValid.Should().BeTrue(
            $"the receiving ServiceToken scheme validates exactly these parameters (failure: {result.Exception?.Message})");
        new JsonWebToken(token).Alg.Should().Be(SecurityAlgorithms.HmacSha256);
    }

    [Fact]
    public void GetToken_IsACompactJwt()
    {
        var sut = CreateSut(ServiceTokenTestData.NewBase64Key(), new FakeTimeProvider(WholeSecondNow()));

        var token = sut.GetToken();

        token.Split('.').Should().HaveCount(3, "a compact JWS has a header, a payload and a signature");
    }

    [Fact]
    public async Task GetToken_DoesNotValidateUnderADifferentKey()
    {
        var sut = CreateSut(ServiceTokenTestData.NewBase64Key(), new FakeTimeProvider(WholeSecondNow()));
        var token = sut.GetToken();

        var result = await new JsonWebTokenHandler()
            .ValidateTokenAsync(token, ServiceTokenTestData.ValidationParameters(ServiceTokenTestData.NewBase64Key()));

        result.IsValid.Should().BeFalse("the signature is the only thing that proves the caller holds the shared key");
    }

    [Fact]
    public void GetToken_CarriesTheServiceNameAsSubAndSvc()
    {
        var sut = CreateSut(ServiceTokenTestData.NewBase64Key(), new FakeTimeProvider(WholeSecondNow()));

        var jwt = new JsonWebToken(sut.GetToken());

        jwt.Subject.Should().Be(ServiceTokenTestData.ServiceName);
        jwt.GetClaim(ServiceTokenDefaults.ServiceClaimType).Value.Should().Be(ServiceTokenTestData.ServiceName);
    }

    [Fact]
    public void GetToken_CarriesIssuerAudienceIatAndJti()
    {
        var now = WholeSecondNow();
        var sut = CreateSut(ServiceTokenTestData.NewBase64Key(), new FakeTimeProvider(now));

        var jwt = new JsonWebToken(sut.GetToken());

        jwt.Issuer.Should().Be(ServiceTokenTestData.Issuer);
        jwt.Audiences.Should().Equal(ServiceTokenTestData.Audience);
        jwt.TryGetPayloadValue<long>("iat", out var iat).Should().BeTrue("the token carries an iat claim");
        iat.Should().Be(now.ToUnixTimeSeconds());
        jwt.Id.Should().NotBeNullOrWhiteSpace("every token carries a jti");
    }

    [Fact]
    public void GetToken_LifetimeIsExactlyLifetimeMinutesFromNow()
    {
        var now = WholeSecondNow();
        var sut = CreateSut(ServiceTokenTestData.NewBase64Key(), new FakeTimeProvider(now));

        var jwt = new JsonWebToken(sut.GetToken());

        (jwt.ValidTo - jwt.ValidFrom).Should().Be(Lifetime);
        jwt.ValidFrom.Should().Be(now.UtcDateTime, "nbf is the injected clock's now, not the wall clock");
        jwt.ValidTo.Should().Be(now.UtcDateTime + Lifetime);
    }

    [Fact]
    public void GetToken_HonorsAConfiguredLifetime()
    {
        var now = WholeSecondNow();
        var sut = CreateSut(ServiceTokenTestData.NewBase64Key(), new FakeTimeProvider(now), lifetimeMinutes: 15);

        var jwt = new JsonWebToken(sut.GetToken());

        (jwt.ValidTo - jwt.ValidFrom).Should().Be(TimeSpan.FromMinutes(15));
    }

    // -- Caching --
    [Fact]
    public void GetToken_CalledTwiceAtTheSameInstant_ReturnsTheIdenticalString()
    {
        var sut = CreateSut(ServiceTokenTestData.NewBase64Key(), new FakeTimeProvider(WholeSecondNow()));

        var first = sut.GetToken();
        var second = sut.GetToken();

        second.Should().Be(first);
    }

    [Fact]
    public void GetToken_BeforeTheRefreshWindow_ReturnsTheIdenticalString()
    {
        var clock = new FakeTimeProvider(WholeSecondNow());
        var sut = CreateSut(ServiceTokenTestData.NewBase64Key(), clock);
        var first = sut.GetToken();

        // 61 seconds of lifetime left: still outside the last-60-seconds refresh window.
        clock.Advance(Lifetime - TimeSpan.FromSeconds(61));

        sut.GetToken().Should().Be(first, "a token with more than 60 seconds left is reused");
    }

    [Fact]
    public void GetToken_AtExactly60SecondsRemaining_MintsANewToken()
    {
        var clock = new FakeTimeProvider(WholeSecondNow());
        var sut = CreateSut(ServiceTokenTestData.NewBase64Key(), clock);
        var first = sut.GetToken();

        clock.Advance(Lifetime - TimeSpan.FromSeconds(60));
        var second = sut.GetToken();

        second.Should().NotBe(first, "the refresh threshold is remaining lifetime <= 60 seconds");
        new JsonWebToken(second).ValidTo.Should().BeAfter(new JsonWebToken(first).ValidTo);
    }

    [Fact]
    public void GetToken_InsideTheLast60Seconds_MintsANewTokenAndThenReusesIt()
    {
        var clock = new FakeTimeProvider(WholeSecondNow());
        var sut = CreateSut(ServiceTokenTestData.NewBase64Key(), clock);
        var first = sut.GetToken();

        clock.Advance(Lifetime - TimeSpan.FromSeconds(30));
        var second = sut.GetToken();
        var third = sut.GetToken();

        second.Should().NotBe(first, "a token that could expire mid-call is never handed out");
        third.Should().Be(second, "the replacement is cached in turn");
        (new JsonWebToken(second).ValidTo - clock.GetUtcNow().UtcDateTime).Should().Be(Lifetime);
    }

    [Fact]
    public void GetToken_AfterExpiry_MintsANewToken()
    {
        var clock = new FakeTimeProvider(WholeSecondNow());
        var sut = CreateSut(ServiceTokenTestData.NewBase64Key(), clock);
        var first = sut.GetToken();

        clock.Advance(Lifetime + TimeSpan.FromMinutes(1));

        sut.GetToken().Should().NotBe(first);
    }

    // -- Scaffolding --

    // JWT times are whole seconds, so the fake clock starts on a second boundary to make the
    // exp/nbf assertions exact. It starts at the real now so wall-clock validation still passes.
    private static DateTimeOffset WholeSecondNow() =>
        DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());

    private static ServiceTokenProvider CreateSut(string key, TimeProvider clock, int lifetimeMinutes = LifetimeMinutes) =>
        new(Options.Create(ServiceTokenTestData.ValidSettings(key, lifetimeMinutes)), clock);
}

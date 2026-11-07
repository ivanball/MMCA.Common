using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MMCA.Common.Application.Auth;
using MMCA.Common.Application.Auth.ViewAs;
using MMCA.Common.Application.Interfaces.Infrastructure.Auth;
using MMCA.Common.Domain.Auth;
using MMCA.Common.Infrastructure.Auth;
using MMCA.Common.Shared.Auth;
using MMCA.Common.Shared.Auth.Permissions;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Auth.ViewAs;

/// <summary>
/// Spec B assertion 2 (T20, ADR-137): the view-as token is minted by the real <see cref="TokenService"/>
/// directly, so it carries no <c>sid</c> and no <c>mfa</c>, expires within 15 minutes even when the
/// configured access-token lifetime is longer, and opens no refresh session.
/// </summary>
public sealed class ViewAsTokenTests
{
    private const UserIdentifierType StaffUserId = 1;
    private const UserIdentifierType TargetUserId = 2;

    private static readonly DateTimeOffset Now = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task StartAsync_MintsATokenWithNoSidNoMfa_ExpiringWithin15Minutes_AndCreatesNoRefreshSession()
    {
        var refreshSessions = new Mock<IRefreshSessionStore>(MockBehavior.Strict);
        var grants = new Mock<IViewAsGrantStore>();
        var subjects = new Mock<IViewAsSubjectResolver>();
        subjects.Setup(s => s.FindAsync(TargetUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ViewAsSubject(TargetUserId, "target@example.com", "Attendee", "Target User", IsDeleted: false));

        var clock = new FakeTimeProvider(Now);
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton(refreshSessions.Object);
        services.AddSingleton(grants.Object);
        services.AddSingleton(subjects.Object);
        services.AddSingleton<IPermissionRegistry>(new PermissionRegistryBuilder().Grant("Attendee", "sessions:read").Build());
        services.AddSingleton(Options.Create(new ViewAsSettings { Enabled = true }));
        services.AddSingleton(Options.Create(new JwtSettings
        {
            SigningAlgorithm = JwtSigningAlgorithm.HS256,
            SecretForKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64)),
            Issuer = "https://test-issuer",
            Audience = "test-audience",

            // Longer than the view-as cap on purpose: the token must still expire within 15 minutes.
            AccessTokenExpirationMinutes = 60,
        }));
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<ITokenService, TokenService>();
        services.AddScoped<IViewAsService, ViewAsService>();
        await using var provider = services.BuildServiceProvider();

        var result = await provider.GetRequiredService<IViewAsService>().StartAsync(StaffUserId, TargetUserId, "Ticket 4411: schedule missing");

        result.IsSuccess.Should().BeTrue();
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(result.Value!.AccessToken);
        jwt.Claims.Should().NotContain(c => c.Type == AuthClaimTypes.SessionId, "a view-as token belongs to no refresh session");
        jwt.Claims.Should().NotContain(c => c.Type == AuthClaimTypes.MultiFactor, "a view-as token never asserts a second factor");
        jwt.Claims.Should().Contain(c => c.Type == JwtRegisteredClaimNames.Sub && c.Value == "2");
        jwt.Claims.Should().Contain(c => c.Type == AuthClaimTypes.ViewAsActor && c.Value == "1");
        jwt.Claims.Should().Contain(c => c.Type == AuthClaimTypes.ViewAsGrant && c.Value == result.Value.GrantId.ToString());
        jwt.Claims.Should().Contain(c => c.Type == AuthClaimTypes.Permission && c.Value == "sessions:read");
        (jwt.ValidTo - Now.UtcDateTime).Should().BeLessThanOrEqualTo(TimeSpan.FromMinutes(15));
        result.Value.AccessTokenExpiry.Should().Be(jwt.ValidTo);

        grants.Verify(g => g.AddAsync(It.Is<ViewAsGrant>(x => x.Id == result.Value.GrantId), It.IsAny<CancellationToken>()), Times.Once);
        refreshSessions.VerifyNoOtherCalls();
    }

    [Fact]
    public void GenerateAccessToken_WithALifetime_HonorsItInsteadOfTheConfiguredOne()
    {
        var clock = new FakeTimeProvider(Now);
        using var sut = new TokenService(
            Options.Create(new JwtSettings
            {
                SigningAlgorithm = JwtSigningAlgorithm.HS256,
                SecretForKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64)),
                Issuer = "https://test-issuer",
                Audience = "test-audience",
                AccessTokenExpirationMinutes = 60,
            }),
            new PermissionRegistryBuilder().Build(),
            clock,
            NullLogger<TokenService>.Instance);

        var token = sut.GenerateShortLivedAccessToken(2, "t@example.com", "Attendee", "T", null, TimeSpan.FromMinutes(7));

        new JwtSecurityTokenHandler().ReadJwtToken(token).ValidTo.Should().Be(Now.UtcDateTime.AddMinutes(7));
    }
}

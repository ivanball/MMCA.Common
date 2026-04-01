using System.Security.Claims;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using MMCA.Common.Infrastructure.Services;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Services;

public sealed class CurrentUserServiceAdditionalTests
{
    private static CurrentUserService CreateSut(ClaimsPrincipal? user = null)
    {
        var httpContextAccessor = new Mock<IHttpContextAccessor>();
        if (user is not null)
        {
            var httpContext = new DefaultHttpContext { User = user };
            httpContextAccessor.Setup(x => x.HttpContext).Returns(httpContext);
        }

        return new CurrentUserService(httpContextAccessor.Object);
    }

    private static ClaimsPrincipal CreatePrincipal(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, "TestAuth"));

    // ── GetClaimValue ──
    [Fact]
    public void GetClaimValue_WithIntClaim_ReturnsValue()
    {
        var principal = CreatePrincipal(new Claim("speaker_id", "42"));
        var sut = CreateSut(principal);

        var result = sut.GetClaimValue<int>("speaker_id");

        result.Should().Be(42);
    }

    [Fact]
    public void GetClaimValue_WithGuidClaim_ReturnsValue()
    {
        var guid = Guid.NewGuid();
        var principal = CreatePrincipal(new Claim("session_id", guid.ToString()));
        var sut = CreateSut(principal);

        var result = sut.GetClaimValue<Guid>("session_id");

        result.Should().Be(guid);
    }

    [Fact]
    public void GetClaimValue_WithMissingClaim_ReturnsNull()
    {
        var principal = CreatePrincipal();
        var sut = CreateSut(principal);

        var result = sut.GetClaimValue<int>("nonexistent");

        result.Should().BeNull();
    }

    [Fact]
    public void GetClaimValue_WithInvalidValue_ReturnsNull()
    {
        var principal = CreatePrincipal(new Claim("speaker_id", "not-a-number"));
        var sut = CreateSut(principal);

        var result = sut.GetClaimValue<int>("speaker_id");

        result.Should().BeNull();
    }

    [Fact]
    public void GetClaimValue_WithNoHttpContext_ReturnsNull()
    {
        var sut = CreateSut();

        var result = sut.GetClaimValue<int>("speaker_id");

        result.Should().BeNull();
    }

    // ── UserId is cached (Lazy<T>) ──
    [Fact]
    public void UserId_CalledMultipleTimes_ReturnsSameValue()
    {
        var principal = CreatePrincipal(new Claim("user_id", "99"));
        var sut = CreateSut(principal);

        var first = sut.UserId;
        var second = sut.UserId;

        first.Should().Be(99);
        second.Should().Be(99);
    }

    // ── Role is cached (Lazy<T>) ──
    [Fact]
    public void Role_CalledMultipleTimes_ReturnsSameValue()
    {
        var principal = CreatePrincipal(new Claim(ClaimTypes.Role, "Speaker"));
        var sut = CreateSut(principal);

        var first = sut.Role;
        var second = sut.Role;

        first.Should().Be("Speaker");
        second.Should().Be("Speaker");
    }

    // ── User property returns principal from HttpContext ──
    [Fact]
    public void User_WithAuthentication_HasIdentity()
    {
        var principal = CreatePrincipal(new Claim("user_id", "1"));
        var sut = CreateSut(principal);

        sut.User.Identity.Should().NotBeNull();
        sut.User.Identity!.IsAuthenticated.Should().BeTrue();
    }
}

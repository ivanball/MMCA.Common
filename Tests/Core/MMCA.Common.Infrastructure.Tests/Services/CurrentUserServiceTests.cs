using System.Security.Claims;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Infrastructure.Services;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Services;

public sealed class CurrentUserServiceTests
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

    [Fact]
    public void UserId_WithValidClaim_ShouldReturnParsedId()
    {
        var principal = CreatePrincipal(new Claim("user_id", "42"));
        var sut = CreateSut(principal);

        sut.UserId.Should().Be(42);
    }

    [Fact]
    public void UserId_WithNoClaim_ShouldReturnNull()
    {
        var principal = CreatePrincipal();
        var sut = CreateSut(principal);

        sut.UserId.Should().BeNull();
    }

    [Fact]
    public void UserId_WithNonNumericClaim_ShouldReturnNull()
    {
        var principal = CreatePrincipal(new Claim("user_id", "not-a-number"));
        var sut = CreateSut(principal);

        sut.UserId.Should().BeNull();
    }

    [Fact]
    public void Role_WithRoleClaim_ShouldReturnRole()
    {
        var principal = CreatePrincipal(new Claim(ClaimTypes.Role, "Organizer"));
        var sut = CreateSut(principal);

        sut.Role.Should().Be("Organizer");
    }

    [Fact]
    public void Role_WithNoRoleClaim_ShouldReturnNull()
    {
        var principal = CreatePrincipal();
        var sut = CreateSut(principal);

        sut.Role.Should().BeNull();
    }

    [Fact]
    public void User_WithNoHttpContext_ShouldReturnEmptyPrincipal()
    {
        var sut = CreateSut();

        sut.User.Should().NotBeNull();
        sut.User.Identity.Should().BeNull();
    }

    [Fact]
    public void User_WithHttpContext_ShouldReturnContextUser()
    {
        var principal = CreatePrincipal(new Claim("user_id", "1"));
        var sut = CreateSut(principal);

        sut.User.Should().BeSameAs(principal);
    }

    [Fact]
    public void AllProperties_WithFullClaims_ShouldReturnCorrectValues()
    {
        var principal = CreatePrincipal(
            new Claim("user_id", "10"),
            new Claim(ClaimTypes.Role, "Attendee"));
        var sut = CreateSut(principal);

        sut.UserId.Should().Be(10);
        sut.Role.Should().Be("Attendee");
    }

    // ── IsInRole must consider every role claim, not just the first ──
    // Comparing against Role alone matched only the first claim, so a principal holding several
    // roles failed the check for all but whichever happened to be listed first. Latent while tokens
    // carry one role, and it would have surfaced as a silent authorization denial. These tests hold
    // the SUT as ICurrentUserService because Roles and IsInRole are default interface members.
    [Theory]
    [InlineData("Attendee")]
    [InlineData("Organizer")]
    public void IsInRole_WithMultipleRoleClaims_MatchesAnyOfThem(string roleName)
    {
        var principal = CreatePrincipal(
            new Claim(ClaimTypes.Role, "Attendee"),
            new Claim(ClaimTypes.Role, "Organizer"));
        ICurrentUserService sut = CreateSut(principal);

        sut.IsInRole(roleName).Should().BeTrue();
    }

    [Fact]
    public void IsInRole_RoleNotHeld_ReturnsFalse()
    {
        var principal = CreatePrincipal(
            new Claim(ClaimTypes.Role, "Attendee"),
            new Claim(ClaimTypes.Role, "Speaker"));
        ICurrentUserService sut = CreateSut(principal);

        sut.IsInRole("Organizer").Should().BeFalse();
    }

    [Theory]
    [InlineData("organizer")]
    [InlineData("ORGANIZER")]
    public void IsInRole_IsCaseInsensitive(string roleName)
    {
        var principal = CreatePrincipal(new Claim(ClaimTypes.Role, "Organizer"));
        ICurrentUserService sut = CreateSut(principal);

        sut.IsInRole(roleName).Should().BeTrue();
    }

    [Theory]
    [InlineData("role")]
    [InlineData("roles")]
    public void IsInRole_HonorsRawRoleClaimTypes_WhenInboundMappingIsDisabled(string claimType)
    {
        var principal = CreatePrincipal(new Claim(claimType, "Organizer"));
        ICurrentUserService sut = CreateSut(principal);

        sut.IsInRole("Organizer").Should().BeTrue();
    }

    [Fact]
    public void IsInRole_Unauthenticated_ReturnsFalse()
    {
        ICurrentUserService sut = CreateSut();

        sut.IsInRole("Organizer").Should().BeFalse();
    }

    [Fact]
    public void Roles_ReturnsEveryRoleClaim()
    {
        var principal = CreatePrincipal(
            new Claim(ClaimTypes.Role, "Attendee"),
            new Claim(ClaimTypes.Role, "Organizer"));
        ICurrentUserService sut = CreateSut(principal);

        sut.Roles.Should().BeEquivalentTo("Attendee", "Organizer");
    }

    [Fact]
    public void Roles_Unauthenticated_IsEmpty()
    {
        ICurrentUserService sut = CreateSut();

        sut.Roles.Should().BeEmpty();
    }
}

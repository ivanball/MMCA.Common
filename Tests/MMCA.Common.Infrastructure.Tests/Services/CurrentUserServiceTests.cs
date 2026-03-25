using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
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
}

using System.Security.Claims;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using MMCA.Common.API.FeatureManagement;

namespace MMCA.Common.API.Tests.FeatureManagement;

/// <summary>
/// Tests for the feature-flag targeting context. The load-bearing decisions are which claim carries
/// the user id (the Targeting filter hashes it, so a rollout is only sticky per user if the id is
/// stable) and that an anonymous request produces an empty context rather than an error, because a
/// feature filter must never be able to fail a request.
/// </summary>
public sealed class CurrentUserTargetingContextAccessorTests
{
    [Fact]
    public async Task GetContextAsync_ForAuthenticatedUser_ReturnsUserIdAndRoleGroups()
    {
        var sut = CreateAccessor(new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("user_id", "42"),
                new Claim(ClaimTypes.Role, "Organizer"),
                new Claim(ClaimTypes.Role, "Attendee"),
            ],
            authenticationType: "TestAuth")));

        var context = await sut.GetContextAsync();

        context.UserId.Should().Be("42");
        context.Groups.Should().BeEquivalentTo("Organizer", "Attendee");
    }

    // Inbound claim mapping can be off, in which case the middleware leaves the raw JWT claim names
    // in place. Reading only ClaimTypes.Role would report no groups and quietly exclude every user
    // from a group-targeted rollout.
    [Theory]
    [InlineData("role")]
    [InlineData("roles")]
    public async Task GetContextAsync_ReadsUnmappedRoleClaimTypesToo(string roleClaimType)
    {
        var sut = CreateAccessor(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("user_id", "42"), new Claim(roleClaimType, "Organizer")],
            authenticationType: "TestAuth")));

        var context = await sut.GetContextAsync();

        context.Groups.Should().BeEquivalentTo("Organizer");
    }

    // A token predating the user_id claim still has to target something stable, or every such
    // caller would collapse into one bucket.
    [Fact]
    public async Task GetContextAsync_WhenUserIdClaimMissing_FallsBackToTheIdentityName()
    {
        var sut = CreateAccessor(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "alice")],
            authenticationType: "TestAuth")));

        var context = await sut.GetContextAsync();

        context.UserId.Should().Be("alice");
        context.Groups.Should().BeEmpty();
    }

    [Fact]
    public async Task GetContextAsync_ForAnonymousRequest_ReturnsAnEmptyContext()
    {
        var sut = CreateAccessor(new ClaimsPrincipal(new ClaimsIdentity()));

        var context = await sut.GetContextAsync();

        context.UserId.Should().BeNull();
        context.Groups.Should().BeEmpty();
    }

    // Background work (a hosted service, an outbox drain) has no HttpContext at all; the accessor
    // is a singleton, so it must answer there rather than throw.
    [Fact]
    public async Task GetContextAsync_OutsideAnyRequest_ReturnsAnEmptyContext()
    {
        var sut = new CurrentUserTargetingContextAccessor(new HttpContextAccessor());

        var context = await sut.GetContextAsync();

        context.UserId.Should().BeNull();
        context.Groups.Should().BeEmpty();
    }

    private static CurrentUserTargetingContextAccessor CreateAccessor(ClaimsPrincipal user)
    {
        var httpContext = new DefaultHttpContext { User = user };
        return new CurrentUserTargetingContextAccessor(new HttpContextAccessor { HttpContext = httpContext });
    }
}

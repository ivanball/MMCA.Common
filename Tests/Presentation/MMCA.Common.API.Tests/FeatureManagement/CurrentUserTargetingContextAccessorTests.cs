using System.Security.Claims;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.API.FeatureManagement;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Shared.Auth;
using Moq;

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
                new Claim(AuthClaimTypes.Subject, "42"),
                new Claim(ClaimTypes.Role, "Manager"),
                new Claim(ClaimTypes.Role, "Member"),
            ],
            authenticationType: "TestAuth")));

        var context = await sut.GetContextAsync();

        context.UserId.Should().Be("42");
        context.Groups.Should().BeEquivalentTo("Manager", "Member");
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
            [new Claim(AuthClaimTypes.Subject, "42"), new Claim(roleClaimType, "Manager")],
            authenticationType: "TestAuth")));

        var context = await sut.GetContextAsync();

        context.Groups.Should().BeEquivalentTo("Manager");
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

    // ADR-073: a resolved tenant joins the caller's own groups, so an audience can name one tenant
    // without losing the role groups an existing rollout already targets.
    [Fact]
    public async Task GetContextAsync_WithResolvedTenant_AddsTheTenantGroupBesideTheRoles()
    {
        var sut = CreateAccessor(
            new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(AuthClaimTypes.Subject, "42"), new Claim(ClaimTypes.Role, "Manager")],
                authenticationType: "TestAuth")),
            tenantId: "acme");

        var context = await sut.GetContextAsync();

        context.UserId.Should().Be("42");
        context.Groups.Should().BeEquivalentTo("Manager", "tenant:acme");
    }

    // A tenant resolved from a header on an anonymous call still targets by tenant.
    [Fact]
    public async Task GetContextAsync_ForAnonymousRequestWithResolvedTenant_CarriesOnlyTheTenantGroup()
    {
        var sut = CreateAccessor(new ClaimsPrincipal(new ClaimsIdentity()), tenantId: "acme");

        var context = await sut.GetContextAsync();

        context.UserId.Should().BeNull();
        context.Groups.Should().BeEquivalentTo("tenant:acme");
    }

    // A host without tenancy (no ITenantContext in the request services, or none resolved) gets
    // exactly the groups it had before tenant targeting existed.
    [Fact]
    public async Task GetContextAsync_WithUnresolvedTenant_AddsNoTenantGroup()
    {
        var sut = CreateAccessor(
            new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(AuthClaimTypes.Subject, "42"), new Claim(ClaimTypes.Role, "Manager")],
                authenticationType: "TestAuth")),
            tenantId: null);

        var context = await sut.GetContextAsync();

        context.Groups.Should().BeEquivalentTo("Manager");
    }

    [Fact]
    public void TenantGroup_PrefixesTheTenantId() =>
        CurrentUserTargetingContextAccessor.TenantGroup("acme").Should().Be("tenant:acme");

    private static CurrentUserTargetingContextAccessor CreateAccessor(ClaimsPrincipal user, string? tenantId = null)
    {
        var services = new ServiceCollection();
        if (tenantId is not null)
        {
            var tenantContext = new Mock<ITenantContext>();
            tenantContext.SetupGet(x => x.TenantId).Returns(tenantId);
            tenantContext.SetupGet(x => x.IsResolved).Returns(true);
            services.AddSingleton(tenantContext.Object);
        }

        var httpContext = new DefaultHttpContext { User = user, RequestServices = services.BuildServiceProvider() };
        return new CurrentUserTargetingContextAccessor(new HttpContextAccessor { HttpContext = httpContext });
    }
}

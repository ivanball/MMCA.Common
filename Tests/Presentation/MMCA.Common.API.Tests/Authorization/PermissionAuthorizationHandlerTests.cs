using System.Security.Claims;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authorization;
using MMCA.Common.API.Authorization;
using MMCA.Common.Shared.Auth;

namespace MMCA.Common.API.Tests.Authorization;

public sealed class PermissionAuthorizationHandlerTests
{
    private const string Permission = "sessions:manage";

    [Fact]
    public async Task HandleAsync_WhenRoleGrantsPermission_Succeeds()
    {
        var context = CreateContext(roles: [RoleNames.Organizer]);
        var handler = CreateHandler(grantTo: RoleNames.Organizer);

        await handler.HandleAsync(context);

        context.HasSucceeded.Should().BeTrue();
    }

    [Fact]
    public async Task HandleAsync_WhenPrincipalHasExplicitPermissionClaim_Succeeds()
    {
        // No role grant configured — the explicit permission claim alone is honored.
        var context = CreateContext(
            roles: [],
            extraClaims: [new Claim(AuthClaimTypes.Permission, Permission)]);
        var handler = CreateHandler(grantTo: RoleNames.Admin);

        await handler.HandleAsync(context);

        context.HasSucceeded.Should().BeTrue();
    }

    [Fact]
    public async Task HandleAsync_WhenRoleDoesNotGrantPermission_DoesNotSucceed()
    {
        var context = CreateContext(roles: [RoleNames.Attendee]);
        var handler = CreateHandler(grantTo: RoleNames.Organizer);

        await handler.HandleAsync(context);

        context.HasSucceeded.Should().BeFalse();
    }

    [Fact]
    public async Task HandleAsync_WhenNotAuthenticated_DoesNotSucceed()
    {
        var context = CreateContext(roles: [RoleNames.Organizer], authenticated: false);
        var handler = CreateHandler(grantTo: RoleNames.Organizer);

        await handler.HandleAsync(context);

        context.HasSucceeded.Should().BeFalse();
    }

    private static PermissionAuthorizationHandler CreateHandler(string grantTo)
    {
        var registry = new PermissionRegistryBuilder()
            .Grant(grantTo, Permission)
            .Build();
        return new PermissionAuthorizationHandler(registry);
    }

    private static AuthorizationHandlerContext CreateContext(
        string[] roles,
        Claim[]? extraClaims = null,
        bool authenticated = true)
    {
        var claims = roles.Select(role => new Claim(ClaimTypes.Role, role)).ToList();
        if (extraClaims is not null)
        {
            claims.AddRange(extraClaims);
        }

        // A non-null authentication type makes ClaimsIdentity.IsAuthenticated true.
        var identity = authenticated
            ? new ClaimsIdentity(claims, authenticationType: "TestAuth")
            : new ClaimsIdentity(claims);
        var user = new ClaimsPrincipal(identity);
        var requirement = new PermissionRequirement(Permission);

        return new AuthorizationHandlerContext([requirement], user, resource: null);
    }
}

using System.Security.Claims;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using MMCA.Common.Infrastructure.Context;
using MMCA.Common.Infrastructure.Persistence.InternalCommands;
using MMCA.Common.Shared.Auth;

namespace MMCA.Common.Infrastructure.Tests.Persistence.InternalCommands;

/// <summary>
/// The origin captured for background work reads the request's principal as the bearer validation
/// left it: a principal whose listed role was withheld for want of a second factor (only the role it
/// kept, its permission, and the withheld markers) yields an origin with neither the withheld role nor
/// the withheld permission, and the markers are never captured as a role or a permission. A principal
/// that carried the factor keeps both.
/// </summary>
public sealed class InternalCommandOriginWithheldRoleTests
{
    private const string Admin = "Admin";
    private const string Member = "Member";
    private const string OrdersManage = "orders:manage";
    private const string ReportsRead = "reports:read";

    [Fact]
    public void Capture_FromAWithheldPrincipal_CarriesNoWithheldRoleOrPermission()
    {
        var origin = Capture(
            new Claim(AuthClaimTypes.Subject, "7"),
            new Claim(ClaimTypes.Role, Member),
            new Claim(AuthClaimTypes.Permission, ReportsRead),
            new Claim(AuthClaimTypes.MultiFactorWithheldRole, Admin),
            new Claim(AuthClaimTypes.MultiFactorWithheldPermission, OrdersManage));

        origin.UserId.Should().Be(7);
        origin.UserRoles.Should().Be(Member);
        JsonSerializer.Deserialize<string[]>(origin.UserPermissions!).Should().Equal(ReportsRead);

        var restored = AmbientOrigin.BuildPrincipal(origin.UserId, origin.UserRoles, origin.UserPermissions, "Test")!;
        restored.HasRole(Admin).Should().BeFalse();
        restored.HasPermissionClaim(OrdersManage).Should().BeFalse();
    }

    [Fact]
    public void Capture_FromAPrincipalWithTheFactor_CarriesTheRoleAndItsPermissions()
    {
        var origin = Capture(
            new Claim(AuthClaimTypes.Subject, "7"),
            new Claim(ClaimTypes.Role, Admin),
            new Claim(ClaimTypes.Role, Member),
            new Claim(AuthClaimTypes.Permission, OrdersManage),
            new Claim(AuthClaimTypes.Permission, ReportsRead),
            new Claim(AuthClaimTypes.MultiFactor, AuthClaimTypes.MultiFactorMethodTotp));

        origin.UserRoles.Should().Be(Admin + "," + Member);
        JsonSerializer.Deserialize<string[]>(origin.UserPermissions!).Should().Equal(OrdersManage, ReportsRead);
    }

    private static InternalCommandOrigin Capture(params Claim[] claims)
    {
        var accessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer")) },
        };

        return new InternalCommandOriginCapture(new CurrentUserService(accessor), new TenantContext(), new CorrelationContext()).Capture();
    }
}

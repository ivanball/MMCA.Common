using System.Globalization;
using System.Security.Claims;
using AwesomeAssertions;
using MMCA.Common.Infrastructure.Context;
using MMCA.Common.Shared.Auth;

namespace MMCA.Common.Infrastructure.Tests.Context;

/// <summary>
/// L181: the stored roles list has a fixed column width, and a list wider than it must lose whole
/// trailing roles rather than end in a cut name, which the restored principal would otherwise carry
/// as a role nobody holds.
/// </summary>
public sealed class AmbientOriginFlattenRolesTests
{
    [Fact]
    public void FlattenRoles_WiderThanTheColumn_DropsWholeTrailingRolesAndNeverCutsAName()
    {
        // 60 roles of 10 characters each: 659 characters joined, so the boundary at 512 falls inside
        // the 47th name.
        string[] roles = [.. Enumerable.Range(0, 60).Select(i => "role-" + i.ToString("D5", CultureInfo.InvariantCulture))];

        var flattened = AmbientOrigin.FlattenRoles(roles);

        flattened.Should().NotBeNull();
        flattened!.Length.Should().BeLessThanOrEqualTo(AmbientOrigin.MaxRolesLength);
        flattened.Split(',').Should().AllSatisfy(
            role => roles.Should().Contain(role, "every restored role must be one the principal really held"));
        flattened.Split(',').Should().Equal(roles.Take(46), "the leading roles that fit whole are kept, in order");
    }

    [Fact]
    public void FlattenRoles_WithinTheColumn_KeepsEveryRole() =>
        AmbientOrigin.FlattenRoles(["Admin", "Manager"]).Should().Be("Admin,Manager");

    // L181: permission claims are carried at full fidelity: far past the roles column width, and a
    // name containing the roles delimiter, every claim comes back whole.
    [Fact]
    public void FlattenPermissions_ThenBuildPrincipal_RestoresEveryPermissionClaimWhole()
    {
        string[] permissions =
        [
            .. Enumerable.Range(0, 60).Select(i => "reports:export-region-" + i.ToString("D8", CultureInfo.InvariantCulture)),
            "odd,name:with-a-comma",
        ];
        var inline = new ClaimsPrincipal(new ClaimsIdentity(
            [.. permissions.Select(permission => new Claim(AuthClaimTypes.Permission, permission))],
            "Bearer"));

        var stored = AmbientOrigin.FlattenPermissions(inline);
        var restored = AmbientOrigin.BuildPrincipal(7, null, stored, "Test");

        stored!.Length.Should().BeGreaterThan(AmbientOrigin.MaxRolesLength, "the scenario must exceed what the roles column holds");
        restored!.FindAll(AuthClaimTypes.Permission).Select(claim => claim.Value).Should().Equal(permissions);
        restored.FindFirst(AuthClaimTypes.MultiFactor).Should().BeNull("the mfa claim is intentionally never propagated");
    }
}

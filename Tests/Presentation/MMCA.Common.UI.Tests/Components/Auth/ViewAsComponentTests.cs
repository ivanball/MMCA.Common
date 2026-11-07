using System.Globalization;
using System.Security.Claims;
using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using MMCA.Common.Shared.Auth;
using MMCA.Common.Shared.Auth.Permissions;
using MMCA.Common.UI.Components.Auth;

namespace MMCA.Common.UI.Tests.Components.Auth;

/// <summary>
/// bUnit tests for <see cref="ViewAsBanner"/> and <see cref="ViewAsButton"/> (T20, ADR-137): each
/// renders only for the principal it is meant for.
/// </summary>
public sealed class ViewAsComponentTests : BunitTestBase
{
    // A prerender renderer: the banner's one-second countdown timer starts only when interactive.
    public ViewAsComponentTests() => SetRendererInfo(new RendererInfo("Static", isInteractive: false));

    [Fact]
    public void Banner_ForAnOrdinaryUser_RendersNothing()
    {
        var cut = RenderWith<ViewAsBanner>(Principal(new Claim(ClaimTypes.Name, "Staff User")));

        cut.FindAll(".mmca-view-as-banner").Should().BeEmpty();
        cut.Markup.Trim().Should().BeEmpty();
    }

    [Fact]
    public void Banner_ForAViewAsUser_ShowsTheViewedUsersName_TheCountdown_AndAnEndAction()
    {
        var expires = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var cut = RenderWith<ViewAsBanner>(Principal(
            new Claim(ClaimTypes.Name, "Target User"),
            new Claim(AuthClaimTypes.ViewAsActor, "1"),
            new Claim(AuthClaimTypes.ViewAsGrant, Guid.NewGuid().ToString()),
            new Claim("exp", expires)));

        var banner = cut.Find(".mmca-view-as-banner");
        banner.TextContent.Should().Contain("Viewing as Target User").And.Contain("read-only");
        banner.GetAttribute("role").Should().Be("status");
        var end = cut.FindAll("button").Should().ContainSingle(b => b.TextContent.Trim() == "End").Subject;

        end.Click();

        JSInterop.VerifyInvoke("mmcaViewAs.end").Arguments.Should().ContainSingle().Which.Should().Be("/api/auth/view-as/end");
    }

    [Fact]
    public void Button_ForACallerHoldingUsersViewAs_Renders()
    {
        var cut = RenderWith<ViewAsButton>(
            Principal(new Claim(AuthClaimTypes.Permission, AdministrationPermissions.ViewAsUsers)),
            p => p.Add(c => c.UserId, 7));

        cut.FindAll(".mmca-view-as-button").Should().ContainSingle();
    }

    [Fact]
    public void Button_ForACallerWithoutUsersViewAs_RendersNothing()
    {
        var cut = RenderWith<ViewAsButton>(
            Principal(new Claim(AuthClaimTypes.Permission, AdministrationPermissions.ManageUsers)),
            p => p.Add(c => c.UserId, 7));

        cut.FindAll(".mmca-view-as-button").Should().BeEmpty();
    }

    [Fact]
    public void Button_ForACallerWhoIsAlreadyViewingAsSomeone_RendersNothing()
    {
        var cut = RenderWith<ViewAsButton>(
            Principal(
                new Claim(AuthClaimTypes.Permission, AdministrationPermissions.ViewAsUsers),
                new Claim(AuthClaimTypes.ViewAsActor, "1")),
            p => p.Add(c => c.UserId, 7));

        cut.FindAll(".mmca-view-as-button").Should().BeEmpty();
    }

    private static ClaimsPrincipal Principal(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, "jwt", ClaimTypes.Name, ClaimTypes.Role));

    private IRenderedComponent<TComponent> RenderWith<TComponent>(
        ClaimsPrincipal user,
        Action<ComponentParameterCollectionBuilder<TComponent>>? parameters = null)
        where TComponent : IComponent =>
        Render<TComponent>(p =>
        {
            p.AddCascadingValue(Task.FromResult(new AuthenticationState(user)));
            parameters?.Invoke(p);
        });
}

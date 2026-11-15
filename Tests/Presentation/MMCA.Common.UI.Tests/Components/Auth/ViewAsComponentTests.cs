using System.Globalization;
using System.Security.Claims;
using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.JSInterop;
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

    [Theory]
    [InlineData("123456789", false)]
    [InlineData("   12345678   ", false)]
    [InlineData("1234567890", true)]
    [InlineData("  1234567890  ", true)]
    public void Button_StartIsEnabledOnlyOnceTheTrimmedReasonHasTenCharacters(string reason, bool enabled)
    {
        var cut = OpenViewAsForm();

        cut.Find("textarea").Input(reason);

        StartButton(cut).HasAttribute("disabled").Should().Be(!enabled);
    }

    [Theory]
    [InlineData(500, true)]
    [InlineData(501, false)]
    public void Button_StartIsDisabledForAReasonLongerThanFiveHundredCharacters(int length, bool enabled)
    {
        var cut = OpenViewAsForm();

        cut.Find("textarea").Input(new string('r', length));

        StartButton(cut).HasAttribute("disabled").Should().Be(!enabled);
    }

    [Fact]
    public void Button_WithNoReasonTyped_KeepsStartDisabled()
    {
        var cut = OpenViewAsForm();

        StartButton(cut).HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void Button_Start_PostsTheTrimmedReasonToTheDefaultProxyRoute_AndShowsNoErrorOnSuccess()
    {
        var start = JSInterop.Setup<int>("mmcaViewAs.start", _ => true);
        start.SetResult(204);
        var cut = OpenViewAsForm();
        cut.Find("textarea").Input("  Customer ticket 4711  ");

        StartButton(cut).Click();

        var invocation = start.VerifyInvoke("mmcaViewAs.start");
        invocation.Arguments.Should().Equal("/api/Admin/Users/7/view-as", "Customer ticket 4711");
        cut.FindAll("[role=alert]").Should().BeEmpty("a 2xx means the script reloads the app, nothing failed");
        StartButton(cut).HasAttribute("disabled").Should().BeFalse("the start is no longer in flight");
    }

    [Fact]
    public void Button_Start_UsesTheConfiguredPrefixAndRoute_TrimmingTheirSlashes()
    {
        var start = JSInterop.Setup<int>("mmcaViewAs.start", _ => true);
        start.SetResult(200);
        var cut = OpenViewAsForm(p => p
            .Add(c => c.ApiPathPrefix, "/gateway/")
            .Add(c => c.UsersRoute, "/Admin/People/"));
        cut.Find("textarea").Input("Customer ticket 4711");

        StartButton(cut).Click();

        start.VerifyInvoke("mmcaViewAs.start").Arguments[0].Should().Be("/gateway/Admin/People/7/view-as");
    }

    [Theory]
    [InlineData(199)]
    [InlineData(300)]
    [InlineData(403)]
    public void Button_Start_WhenTheGrantIsRefused_ShowsTheRefusal(int status)
    {
        JSInterop.Setup<int>("mmcaViewAs.start", _ => true).SetResult(status);
        var cut = OpenViewAsForm();
        cut.Find("textarea").Input("Customer ticket 4711");

        StartButton(cut).Click();

        cut.Find("[role=alert]").TextContent.Should().Be("Viewing as this user was refused.");
        StartButton(cut).HasAttribute("disabled").Should().BeFalse("a refused start can be retried");
    }

    [Fact]
    public void Button_Start_WhenTheScriptThrows_ShowsTheRefusal()
    {
        JSInterop.Setup<int>("mmcaViewAs.start", _ => true).SetException(new JSException("script failed"));
        var cut = OpenViewAsForm();
        cut.Find("textarea").Input("Customer ticket 4711");

        StartButton(cut).Click();

        cut.Find("[role=alert]").TextContent.Should().Be("Viewing as this user was refused.");
    }

    [Fact]
    public void Button_Start_WhileTheGrantIsInFlight_DisablesStart()
    {
        // No result set: the script call stays pending, as it does while the request is out.
        JSInterop.Setup<int>("mmcaViewAs.start", _ => true);
        var cut = OpenViewAsForm();
        cut.Find("textarea").Input("Customer ticket 4711");

        StartButton(cut).Click();

        StartButton(cut).HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void Button_Start_AfterARefusal_ClearsThePreviousErrorOnTheNextSuccess()
    {
        var start = JSInterop.Setup<int>("mmcaViewAs.start", _ => true);
        start.SetResult(403);
        var cut = OpenViewAsForm();
        cut.Find("textarea").Input("Customer ticket 4711");
        StartButton(cut).Click();
        cut.FindAll("[role=alert]").Should().ContainSingle();

        start.SetResult(204);
        StartButton(cut).Click();

        cut.FindAll("[role=alert]").Should().BeEmpty();
    }

    private static AngleSharp.Dom.IElement StartButton(IRenderedComponent<ViewAsButton> cut) =>
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Start viewing");

    private IRenderedComponent<ViewAsButton> OpenViewAsForm(
        Action<ComponentParameterCollectionBuilder<ViewAsButton>>? parameters = null)
    {
        var cut = RenderWith<ViewAsButton>(
            Principal(new Claim(AuthClaimTypes.Permission, AdministrationPermissions.ViewAsUsers)),
            p =>
            {
                p.Add(c => c.UserId, 7);
                parameters?.Invoke(p);
            });
        cut.Find(".mmca-view-as-button").Click();
        return cut;
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

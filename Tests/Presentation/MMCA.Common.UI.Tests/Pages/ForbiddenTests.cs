using System.Globalization;
using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Testing.UI;
using MMCA.Common.UI.Pages;

namespace MMCA.Common.UI.Tests.Pages;

/// <summary>
/// Render tests for the in-shell 403 page (rubric §25). A signed-in visitor is told they lack the
/// permission and offered a way back home; a signed-out visitor is told to sign in and offered a
/// sign-in link that returns to the page they asked for. Both keep the h1 and put role="alert" on
/// the message, never on the heading.
/// </summary>
public sealed class ForbiddenTests : BunitTestBase
{
    [Fact]
    public void SignedIn_RendersThePermissionMessageAndHomeLink()
    {
        var cut = RenderAs<Forbidden>(TestPrincipal.AuthenticatedUser(), _ => { });

        cut.Find("h1").TextContent.Should().Contain("Access Denied");
        cut.Find("h1").HasAttribute("role").Should().BeFalse();
        cut.Find("[role=\"alert\"]").TextContent.Should().Contain("You are signed in, but you do not have permission");
        cut.Markup.Should().Contain("href=\"/\"");
        cut.Markup.Should().NotContain("/login");
    }

    [Fact]
    public void SignedOut_OnTheForbiddenRoute_RendersTheSignInMessageAndAPlainLoginLink()
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo("/forbidden");

        var cut = RenderUnderTest<Forbidden>(_ => { });

        cut.Find("h1").TextContent.Should().Contain("Access Denied");
        cut.Find("h1").HasAttribute("role").Should().BeFalse();
        var alert = cut.Find("[role=\"alert\"]").TextContent;
        alert.Should().Contain("You need to sign in to view this page.");
        alert.Should().NotContain("You are signed in");
        cut.Find("a[href^=\"/login\"]").GetAttribute("href").Should().Be("/login", "returning to /forbidden after sign-in would be a dead end");
        cut.Markup.Should().Contain("Sign In");
    }

    // NotFound renders this page under the ORIGINAL url on a 403 re-execution, so sign-in returns there.
    [Fact]
    public void SignedOut_UnderAnotherUrl_TheLoginLinkCarriesThatUrlAsReturnUrl()
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo("/admin/users?page=2");

        var cut = RenderUnderTest<Forbidden>(_ => { });

        cut.Find("a[href^=\"/login\"]").GetAttribute("href").Should().Be("/login?returnUrl=%2Fadmin%2Fusers%3Fpage%3D2");
    }

    [Fact]
    public void SignedOut_InSpanish_TheMessageIsTranslated()
    {
        var previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = new CultureInfo("es");
        try
        {
            var cut = RenderUnderTest<Forbidden>(_ => { });

            cut.Find("[role=\"alert\"]").TextContent.Should().Contain("Es necesario iniciar sesión para ver esta página.");
            cut.Markup.Should().Contain("Iniciar sesión");
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }
}

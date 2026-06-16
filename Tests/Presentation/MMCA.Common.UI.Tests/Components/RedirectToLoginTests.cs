using AwesomeAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.UI.Components;

namespace MMCA.Common.UI.Tests.Components;

/// <summary>
/// bUnit tests for <see cref="RedirectToLogin"/> — verifies it redirects to the login page on init
/// and carries a sanitized <c>returnUrl</c> for the originating protected page.
/// </summary>
public sealed class RedirectToLoginTests : BunitTestBase
{
    [Fact]
    public void WhenAtRoot_RedirectsToLoginWithoutReturnUrl()
    {
        var nav = Services.GetRequiredService<NavigationManager>();

        RenderUnderTest<RedirectToLogin>(_ => { });

        nav.Uri.Should().Be("http://localhost/login");
    }

    [Fact]
    public void WhenOnProtectedPage_RedirectsToLoginWithEncodedReturnUrl()
    {
        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo("/events/5");

        RenderUnderTest<RedirectToLogin>(_ => { });

        nav.Uri.Should().Be("http://localhost/login?returnUrl=" + Uri.EscapeDataString("/events/5"));
    }
}

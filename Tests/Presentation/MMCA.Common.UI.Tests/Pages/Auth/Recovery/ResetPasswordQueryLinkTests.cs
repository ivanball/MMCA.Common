using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.UI.Pages.Auth;
using MMCA.Common.UI.Services.Auth;
using Moq;

namespace MMCA.Common.UI.Tests.Pages.Auth.Recovery;

/// <summary>
/// bUnit tests for a new <c>/reset-password</c> link opened on the page already showing when the link
/// differs only in its QUERY STRING (Store Run 5 R6b): its address and token replace the stale ones, the
/// form is prefilled once, and the email and token are taken out of the address bar, as a new fragment
/// already is. A location change that carries no link pair changes nothing.
/// </summary>
public sealed class ResetPasswordQueryLinkTests : BunitTestBase
{
    public ResetPasswordQueryLinkTests() => Services.AddSingleton(new Mock<IAuthUIService>().Object);

    private NavigationManager Navigation => Services.GetRequiredService<NavigationManager>();

    [Fact]
    public async Task NewLinkInAChangedQuery_OnTheOpenPage_ReplacesTheStaleValues_AndLeavesTheAddressBar()
    {
        var cut = RenderPage("/reset-password?email=old%40example.com&token=expired-1");
        await cut.WaitForAssertionAsync(() => TokenValue(cut).Should().Be("expired-1"));

        await cut.InvokeAsync(() => Navigation.NavigateTo("/reset-password?email=grace%40example.com&token=new-1&x=1"));

        await cut.WaitForAssertionAsync(() =>
        {
            EmailValue(cut).Should().Be("grace@example.com");
            TokenValue(cut).Should().Be("new-1");
        });
        var query = new Uri(Navigation.Uri).Query;
        query.Should().NotContain("email=").And.NotContain("token=").And.NotContain("new-1");
        query.Should().Contain("x=1", "only the link's own parameters are taken out of the address bar");
    }

    [Fact]
    public async Task NewLinkInAChangedQuery_CarryingOnlyTheToken_ClearsTheOldAddress()
    {
        var cut = RenderPage("/reset-password?email=old%40example.com&token=expired-1");
        await cut.WaitForAssertionAsync(() => TokenValue(cut).Should().Be("expired-1"));

        await cut.InvokeAsync(() => Navigation.NavigateTo("/reset-password?token=new-1"));

        await cut.WaitForAssertionAsync(() =>
        {
            TokenValue(cut).Should().Be("new-1");
            EmailValue(cut).Should().BeNullOrEmpty("nothing of the old link survives a new one");
        });
        new Uri(Navigation.Uri).Query.Should().NotContain("token=");
    }

    // Prefilled once: after the new link was read, the user's own correction of the token is not
    // overwritten again, neither by the scrubbed address nor by a later navigation carrying no link.
    [Fact]
    public async Task NewLinkInAChangedQuery_IsPrefilledOnce_AndALaterLocationChangeWithoutALinkLeavesTheForm()
    {
        var cut = RenderPage("/reset-password?email=old%40example.com&token=expired-1");
        await cut.WaitForAssertionAsync(() => TokenValue(cut).Should().Be("expired-1"));
        await cut.InvokeAsync(() => Navigation.NavigateTo("/reset-password?email=grace%40example.com&token=new-1"));
        await cut.WaitForAssertionAsync(() => TokenValue(cut).Should().Be("new-1"));

        await cut.Find("input[autocomplete=one-time-code]").InputAsync(new ChangeEventArgs { Value = "typed-by-hand" });
        await cut.Find("input[autocomplete=one-time-code]").BlurAsync(new FocusEventArgs());
        await cut.WaitForAssertionAsync(() => TokenValue(cut).Should().Be("typed-by-hand"));
        await cut.InvokeAsync(() => Navigation.NavigateTo("/reset-password?x=2"));
        await cut.InvokeAsync(() => { });

        TokenValue(cut).Should().Be("typed-by-hand");
        EmailValue(cut).Should().Be("grace@example.com");
        Navigation.Uri.Should().EndWith("/reset-password?x=2", "nothing was taken out of the address bar");
    }

    [Fact]
    public async Task LocationChange_WithNeitherAFragmentNorALinkPairInTheQuery_DoesNothing()
    {
        var cut = RenderPage("/reset-password?email=old%40example.com&token=t-1");
        await cut.WaitForAssertionAsync(() => TokenValue(cut).Should().Be("t-1"));

        await cut.InvokeAsync(() => Navigation.NavigateTo("/reset-password?lang=en"));
        await cut.InvokeAsync(() => { });

        EmailValue(cut).Should().Be("old@example.com");
        TokenValue(cut).Should().Be("t-1");
        Navigation.Uri.Should().EndWith("/reset-password?lang=en");
    }

    private static string? EmailValue(IRenderedComponent<ResetPassword> cut) =>
        cut.Find("input[autocomplete=email]").GetAttribute("value");

    private static string? TokenValue(IRenderedComponent<ResetPassword> cut) =>
        cut.Find("input[autocomplete=one-time-code]").GetAttribute("value");

    private IRenderedComponent<ResetPassword> RenderPage(string relativeUri)
    {
        Navigation.NavigateTo(relativeUri);
        return RenderUnderTest<ResetPassword>(_ => { });
    }
}

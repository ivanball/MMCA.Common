using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.UI.Pages.Auth;
using MMCA.Common.UI.Services.Auth;
using MMCA.Common.UI.Services.Capabilities;
using Moq;

namespace MMCA.Common.UI.Tests.Pages.Auth.Recovery;

/// <summary>
/// bUnit tests for the <c>/reset-password</c> prefill: the emailed link carries the address and the
/// single-use token in the URL fragment, read once through the capabilities module's
/// <c>locationHashTake</c>, decoded, and copied only into fields that are still empty. Without the
/// module (prerender, a host without the capabilities set) the query-string prefill still works.
/// </summary>
public sealed class ResetPasswordFragmentTests : BunitTestBase
{
    private const string CapabilitiesModulePath = "./_content/MMCA.Common.UI/capabilities-interop.js";

    public ResetPasswordFragmentTests() => Services.AddSingleton(new Mock<IAuthUIService>().Object);

    [Fact]
    public void Fragment_PrefillsTheEmailAndToken_Decoded()
    {
        var take = UseFragment("email=grace%40example.com&token=tok%2B1%3D");

        var cut = RenderPage("/reset-password");

        cut.WaitForAssertion(() =>
        {
            EmailValue(cut).Should().Be("grace@example.com");
            TokenValue(cut).Should().Be("tok+1=");
        });
        take.VerifyInvoke("locationHashTake");
    }

    [Fact]
    public void Fragment_SkipsMalformedAndUnknownPairs_AndMatchesNamesExactly()
    {
        UseFragment("=orphan&novalue&Email=wrong%40example.com&lang=en&token=t-9&email=grace%40example.com");

        var cut = RenderPage("/reset-password");

        cut.WaitForAssertion(() =>
        {
            EmailValue(cut).Should().Be("grace@example.com");
            TokenValue(cut).Should().Be("t-9");
        });
    }

    [Fact]
    public void Fragment_OnlyFillsEmptyFields_SoTheQueryAndTheFirstPairWin()
    {
        UseFragment("email=fragment%40example.com&token=first&token=second");

        var cut = RenderPage("/reset-password?email=query%40example.com");

        cut.WaitForAssertion(() =>
        {
            EmailValue(cut).Should().Be("query@example.com");
            TokenValue(cut).Should().Be("first");
        });
    }

    [Fact]
    public void Fragment_WithOnlyUnknownPairs_LeavesTheQueryPrefill()
    {
        UseFragment("lang=en&utm=mail");

        var cut = RenderPage("/reset-password?email=query%40example.com&token=q-1");

        EmailValue(cut).Should().Be("query@example.com");
        TokenValue(cut).Should().Be("q-1");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankFragment_LeavesTheFieldsEmpty(string fragment)
    {
        UseFragment(fragment);

        var cut = RenderPage("/reset-password");

        EmailValue(cut).Should().BeNullOrEmpty();
        TokenValue(cut).Should().BeNullOrEmpty();
    }

    [Fact]
    public void WithoutTheCapabilitiesModule_TheQueryPrefillStillWorks_AndNoScriptRuns()
    {
        var cut = RenderPage("/reset-password?email=query%40example.com&token=q-1");

        EmailValue(cut).Should().Be("query@example.com");
        TokenValue(cut).Should().Be("q-1");
        JSInterop.Invocations.Should().NotContain(i => i.Identifier == "locationHashTake");
    }

    [Fact]
    public void Fragment_IsTakenOnlyOnTheFirstRender()
    {
        var take = UseFragment("email=grace%40example.com&token=t-1");
        var cut = RenderPage("/reset-password");
        cut.WaitForAssertion(() => TokenValue(cut).Should().Be("t-1"));

        cut.Render();

        take.VerifyInvoke("locationHashTake", calledTimes: 1);
    }

    private static string? EmailValue(IRenderedComponent<ResetPassword> cut) =>
        cut.Find("input[type=email]").GetAttribute("value");

    private static string? TokenValue(IRenderedComponent<ResetPassword> cut) =>
        cut.Find("input[autocomplete=one-time-code]").GetAttribute("value");

    private BunitJSModuleInterop UseFragment(string fragment)
    {
        Services.AddScoped<CapabilitiesJsModule>();
        var module = JSInterop.SetupModule(CapabilitiesModulePath);
        module.Setup<string?>("locationHashTake").SetResult(fragment);
        return module;
    }

    private IRenderedComponent<ResetPassword> RenderPage(string relativeUri)
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo(relativeUri);
        return RenderUnderTest<ResetPassword>(_ => { });
    }
}

using System.Globalization;
using System.Reflection;
using AwesomeAssertions;
using Microsoft.JSInterop;
using MMCA.Common.Shared.Globalization;
using MMCA.Common.UI.Services.Culture;
using Moq;

namespace MMCA.Common.UI.Tests.Services.Culture;

/// <summary>
/// X-03 (Store run 1): in Development the server prerenders under the qps-Ploc pseudo locale
/// (<c>MapCultureEndpoint</c> and request localization permit it there), but the WebAssembly
/// bootstrap accepted only <see cref="SupportedCultures.IsSupported"/>, so hydration reverted the
/// page to en-US. The bootstrap must keep qps-Ploc from the culture cookie when the host says
/// pseudo-localization is allowed (a Development WASM host), and still fall back to the default
/// everywhere else.
/// <para>
/// The bootstrap's current signature carries no environment, so the requirement needs a way for the
/// WASM <c>Program.cs</c> to say "Development". These tests look for the overload
/// <c>SetBrowserCultureAsync(IJSRuntime jsRuntime, bool allowPseudoLocale)</c> by reflection (so the
/// test project keeps compiling before it exists) and fail naming it while it is missing.
/// </para>
/// </summary>
[Collection(CultureMutatingCollection.Name)]
public sealed class MmcaCultureBootstrapTests : IDisposable
{
    private readonly CultureInfo? _originalCulture = CultureInfo.DefaultThreadCurrentCulture;
    private readonly CultureInfo? _originalUiCulture = CultureInfo.DefaultThreadCurrentUICulture;

    [Fact]
    public async Task PseudoLocaleCookie_WhenPseudoLocalizationIsAllowed_KeepsQpsPloc()
    {
        await InvokeWithAllowPseudoAsync(CookieRuntime(SupportedCultures.PseudoLocale), allowPseudoLocale: true);

        CultureInfo.DefaultThreadCurrentUICulture!.Name.Should().Be(
            SupportedCultures.PseudoLocale,
            "a Development WASM host must hydrate in the same pseudo locale the server prerendered");
        CultureInfo.DefaultThreadCurrentCulture!.Name.Should().Be(SupportedCultures.PseudoLocale);
    }

    [Fact]
    public async Task PseudoLocaleCookie_WhenPseudoLocalizationIsNotAllowed_FallsBackToTheDefault()
    {
        await InvokeWithAllowPseudoAsync(CookieRuntime(SupportedCultures.PseudoLocale), allowPseudoLocale: false);

        CultureInfo.DefaultThreadCurrentUICulture!.Name.Should().Be(
            SupportedCultures.Default,
            "outside Development the pseudo locale is never a culture a user can end up in");
    }

    // Regression guard on the existing entry point: it has no environment, so it keeps rejecting
    // the pseudo locale exactly as today.
    [Fact]
    public async Task ExistingOverload_PseudoLocaleCookie_FallsBackToTheDefault()
    {
        await MmcaCultureBootstrap.SetBrowserCultureAsync(CookieRuntime(SupportedCultures.PseudoLocale));

        CultureInfo.DefaultThreadCurrentUICulture!.Name.Should().Be(SupportedCultures.Default);
    }

    public void Dispose()
    {
        CultureInfo.DefaultThreadCurrentCulture = _originalCulture;
        CultureInfo.DefaultThreadCurrentUICulture = _originalUiCulture;
    }

    private static async Task InvokeWithAllowPseudoAsync(IJSRuntime jsRuntime, bool allowPseudoLocale)
    {
        var overload = typeof(MmcaCultureBootstrap).GetMethod(
            nameof(MmcaCultureBootstrap.SetBrowserCultureAsync),
            BindingFlags.Public | BindingFlags.Static,
            [typeof(IJSRuntime), typeof(bool)]);

        overload.Should().NotBeNull(
            "MmcaCultureBootstrap needs SetBrowserCultureAsync(IJSRuntime jsRuntime, bool allowPseudoLocale) so a "
            + "Development WASM host can keep the qps-Ploc culture its server prerendered");

        await (Task)overload!.Invoke(null, [jsRuntime, allowPseudoLocale])!;
    }

    // A JS runtime whose culture.js module reports the given culture cookie value.
    private static IJSRuntime CookieRuntime(string? cookieCulture)
    {
        var module = new Mock<IJSObjectReference>();
        module.Setup(m => m.InvokeAsync<string?>("getCulture", It.IsAny<object?[]?>()))
            .ReturnsAsync(cookieCulture);

        var runtime = new Mock<IJSRuntime>();
        runtime.Setup(r => r.InvokeAsync<IJSObjectReference>("import", It.IsAny<object?[]?>()))
            .ReturnsAsync(module.Object);

        return runtime.Object;
    }
}

/// <summary>
/// Serializes the tests that assign <see cref="CultureInfo.DefaultThreadCurrentCulture"/>: the
/// default applies to every thread created afterwards, so running them beside other tests would leak
/// the culture into whatever those tests start.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CultureMutatingCollection
{
    public const string Name = "Culture-mutating tests";
}

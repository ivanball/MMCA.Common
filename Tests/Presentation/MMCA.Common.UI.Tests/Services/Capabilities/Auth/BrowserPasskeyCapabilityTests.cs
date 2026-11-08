using AwesomeAssertions;
using Bunit;
using MMCA.Common.UI.Services.Capabilities.Auth;

namespace MMCA.Common.UI.Tests.Services.Capabilities.Auth;

/// <summary>
/// <see cref="BrowserPasskeyCapability"/> at the JS-interop boundary (ADR-139): whether autofill is
/// available is the answer of <c>passkeys.js</c>'s <c>isAutofillAvailable</c> (PublicKeyCredential exists
/// and <c>isConditionalMediationAvailable()</c> is true), and a browser where JS cannot answer counts as
/// unavailable, so the sign-in page stores no challenge it cannot use.
/// </summary>
public sealed class BrowserPasskeyCapabilityTests : BunitTestBase
{
    private const string ModulePath = "./_content/MMCA.Common.UI/passkeys.js";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task IsAutofillAvailable_IsTheModulesAnswer(bool browserSupportsIt)
    {
        JSInterop.Mode = JSRuntimeMode.Strict;
        var module = JSInterop.SetupModule(ModulePath);
        module.Setup<bool>("isAutofillAvailable").SetResult(browserSupportsIt);
        await using var capability = new BrowserPasskeyCapability(JSInterop.JSRuntime);

        var available = await capability.IsAutofillAvailableAsync(Xunit.TestContext.Current.CancellationToken);

        available.Should().Be(browserSupportsIt);
        module.VerifyInvoke("isAutofillAvailable");
    }

    [Fact]
    public async Task IsAutofillAvailable_WhenTheBrowserThrows_IsFalse()
    {
        JSInterop.Mode = JSRuntimeMode.Strict;
        var module = JSInterop.SetupModule(ModulePath);
        module.Setup<bool>("isAutofillAvailable").SetException(new Microsoft.JSInterop.JSException("PublicKeyCredential is not defined"));
        await using var capability = new BrowserPasskeyCapability(JSInterop.JSRuntime);

        (await capability.IsAutofillAvailableAsync(Xunit.TestContext.Current.CancellationToken)).Should().BeFalse();
    }
}

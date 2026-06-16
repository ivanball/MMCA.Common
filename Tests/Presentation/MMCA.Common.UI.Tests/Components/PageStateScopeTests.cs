using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Components;
using MMCA.Common.UI.Components;

namespace MMCA.Common.UI.Tests.Components;

/// <summary>
/// bUnit tests for <see cref="PageStateScope"/> — verifies the JS-invoked bfcache-restore entry
/// point forwards to the <c>OnRestore</c> callback the host page supplies.
/// </summary>
public sealed class PageStateScopeTests : BunitTestBase
{
    [Fact]
    public async Task OnPageRestored_InvokesOnRestoreCallback()
    {
        var restored = false;
        var cut = RenderUnderTest<PageStateScope>(p => p
            .Add(c => c.OnRestore, EventCallback.Factory.Create(this, () => restored = true)));

        // JS would invoke this on a back-forward-cache restore; call it directly.
        await cut.InvokeAsync(() => cut.Instance.OnPageRestoredAsync());

        restored.Should().BeTrue();
    }

    [Fact]
    public void RegistersBfcacheHandler_ViaJsModuleImport()
    {
        RenderUnderTest<PageStateScope>(p => p
            .Add(c => c.OnRestore, EventCallback.Empty));

        // First render imports the nav-interop module and registers the handler (loose JSInterop).
        JSInterop.VerifyInvoke("import");
    }
}

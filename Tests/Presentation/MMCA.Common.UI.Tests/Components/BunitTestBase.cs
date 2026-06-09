using Bunit;
using Microsoft.AspNetCore.Components;
using MudBlazor.Services;

namespace MMCA.Common.UI.Tests.Components;

/// <summary>
/// Base for bUnit component tests of the shared UI primitives. Registers MudBlazor services and
/// puts JSInterop in loose mode so MudBlazor components that probe JS during render don't throw.
/// </summary>
/// <remarks>
/// Written against bUnit v2 (the line compatible with xUnit v3 / Microsoft Testing Platform):
/// the context base is <c>BunitContext</c> and the render entry point is <c>Render&lt;T&gt;</c>.
/// If a restore resolves bUnit v1.x instead, the ONLY changes needed are here: base class
/// <c>TestContext</c> and <c>Render</c> → <c>RenderComponent</c>. Individual test classes call
/// <see cref="RenderUnderTest{TComponent}"/> and never reference the version-specific symbols.
/// </remarks>
public abstract class BunitTestBase : BunitContext
{
    protected BunitTestBase()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    protected IRenderedComponent<TComponent> RenderUnderTest<TComponent>(
        Action<ComponentParameterCollectionBuilder<TComponent>> parameters)
        where TComponent : IComponent
        => Render(parameters);
}

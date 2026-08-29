using AwesomeAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Testing.UI;
using MMCA.Common.UI.Common.Interfaces;
using MMCA.Common.UI.Services;

namespace MMCA.Common.UI.Tests.Infrastructure;

/// <summary>
/// Guards the registration contract of the shipped bUnit base: a component that injects the
/// vendor-neutral toast/dialog facades must render under <see cref="BunitComponentTestBase"/> with no
/// per-repo setup. Deliberately derived from the SHIPPED base rather than this repo's
/// <c>BunitTestBase</c>, because the repo-local base is exactly the layer that used to paper over the
/// gap: every consumer that rendered a migrated page had to re-register the pair itself.
/// </summary>
public sealed class BunitComponentTestBaseFacadeTests : BunitComponentTestBase
{
    [Fact]
    public void AComponentInjectingTheToastFacadeRendersUnderTheShippedBase()
    {
        var component = RenderUnderTest<ToastConsumer>(_ => { });

        // The Mud-backed implementation, resolved over the ISnackbar AddMudServices registered:
        // the real path a consumer's component test exercises.
        component.Markup.Should().Contain(nameof(MudToastService));
    }

    [Fact]
    public void TheConfirmDialogFacadeResolvesToo() =>
        Services.GetRequiredService<IAppDialogService>().Should().BeOfType<MudAppDialogService>();

    /// <summary>Minimal component whose only job is to prove <see cref="IToastService"/> injects.</summary>
    private sealed class ToastConsumer : ComponentBase
    {
        [Inject]
        public IToastService Toast { get; set; } = null!;

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenElement(0, "span");
            builder.AddContent(1, Toast.GetType().Name);
            builder.CloseElement();
        }
    }
}

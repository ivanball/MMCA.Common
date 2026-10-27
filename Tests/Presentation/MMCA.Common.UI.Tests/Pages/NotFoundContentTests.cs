using System.Reflection;
using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Testing.UI;
using MMCA.Common.UI.Common;
using MMCA.Common.UI.Common.Interfaces;
using MMCA.Common.UI.Pages;

namespace MMCA.Common.UI.Tests.Pages;

/// <summary>
/// <see cref="IUIModule.NotFoundContentType"/>: an app's own not-found content replaces the framework's
/// default content on <see cref="NotFound"/>, the first module that supplies one wins, a re-executed 403
/// still renders Access Denied, and with no module supplying one the page is unchanged.
/// </summary>
public sealed class NotFoundContentTests : BunitTestBase
{
    // SharedResource.resx "Page.NotFound.Message" / "Page.Forbidden.Title".
    private const string DefaultMessage = "the page you are looking for";
    private const string ForbiddenTitle = "Access Denied";

    [Fact]
    public void IUIModule_NotFoundContentType_DefaultsToNull()
    {
        IUIModule module = new BareModule();

        module.NotFoundContentType.Should().BeNull("a module that supplies no 404 keeps the framework's");
    }

    [Fact]
    public void WithAModuleSupplyingContent_RendersItInPlaceOfTheDefault()
    {
        Services.AddSingleton<IUIModule>(new BareModule());
        Services.AddSingleton<IUIModule>(new NotFoundModule(typeof(FirstProbe)));
        Services.AddSingleton<IUIModule>(new NotFoundModule(typeof(SecondProbe)));

        var cut = RenderAs<NotFound>(TestPrincipal.AuthenticatedUser(), _ => { });

        cut.FindAll("[data-testid=first-probe]").Should().ContainSingle("the first module that supplies content wins");
        cut.FindAll("[data-testid=second-probe]").Should().BeEmpty();
        cut.Markup.Should().NotContain(DefaultMessage, "the app's content replaces the default content");
    }

    [Fact]
    public void WithNoModuleSupplyingContent_RendersTheDefault()
    {
        Services.AddSingleton<IUIModule>(new BareModule());

        var cut = RenderAs<NotFound>(TestPrincipal.AuthenticatedUser(), _ => { });

        cut.Markup.Should().Contain(DefaultMessage);
    }

    [Fact]
    public void WhenReExecutedForA403_StillRendersAccessDenied()
    {
        Services.AddSingleton<IUIModule>(new NotFoundModule(typeof(FirstProbe)));
        var context = new DefaultHttpContext();
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        Services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor { HttpContext = context });

        var cut = RenderAs<NotFound>(TestPrincipal.AuthenticatedUser(), p => p.AddCascadingValue<HttpContext>(context));

        cut.Markup.Should().Contain(ForbiddenTitle, "an app's 404 content never replaces the Access Denied view");
        cut.FindAll("[data-testid=first-probe]").Should().BeEmpty();
    }

    private sealed class BareModule : IUIModule
    {
        public IReadOnlyList<NavItem> NavItems { get; } = [];

        public Assembly Assembly => typeof(BareModule).Assembly;
    }

    private sealed class NotFoundModule(Type contentType) : IUIModule
    {
        public IReadOnlyList<NavItem> NavItems { get; } = [];

        public Assembly Assembly => typeof(NotFoundModule).Assembly;

        public Type? NotFoundContentType => contentType;
    }

    private sealed class FirstProbe : ComponentBase
    {
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenElement(0, "div");
            builder.AddAttribute(1, "data-testid", "first-probe");
            builder.CloseElement();
        }
    }

    private sealed class SecondProbe : ComponentBase
    {
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenElement(0, "div");
            builder.AddAttribute(1, "data-testid", "second-probe");
            builder.CloseElement();
        }
    }
}

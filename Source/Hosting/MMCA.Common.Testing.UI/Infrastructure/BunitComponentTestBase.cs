using System.Security.Claims;
using Bunit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;

namespace MMCA.Common.Testing.UI;

/// <summary>
/// Shared base for bUnit component tests across MMCA repos. Registers MudBlazor services, puts
/// JSInterop in loose mode (so MudBlazor components that probe JS during render don't throw), and
/// wires permissive-but-real auth test doubles so <c>&lt;AuthorizeView&gt;</c> and pages that inject
/// <see cref="AuthenticationStateProvider"/> directly both work. Anonymous by default; pass an
/// authenticated <see cref="ClaimsPrincipal"/> (see <see cref="TestPrincipal"/>) to
/// <see cref="RenderAs{TComponent}"/> to exercise the authorized branch.
/// </summary>
/// <remarks>
/// The provider is <b>mutable</b> (a superset of a hardcoded-anonymous one) so it serves both
/// cascading-<c>AuthenticationState</c> consumers and pages that call
/// <see cref="AuthenticationStateProvider.GetAuthenticationStateAsync"/> on the injected service.
/// <para>
/// Written against bUnit v2 (the line compatible with xUnit v3 / Microsoft Testing Platform): the
/// context base is <c>BunitContext</c> and the render entry point is <c>Render&lt;T&gt;</c>. If a
/// restore ever resolves bUnit v1.x instead, the ONLY changes needed are here (base class
/// <c>TestContext</c>, <c>Render</c> → <c>RenderComponent</c>); derived test classes call
/// <see cref="RenderUnderTest{TComponent}"/> / <see cref="RenderAs{TComponent}"/> and never touch the
/// version-specific symbols.
/// </para>
/// </remarks>
public abstract class BunitComponentTestBase : BunitContext
{
    /// <summary>An unauthenticated principal (no authentication type ⇒ <c>IsAuthenticated == false</c>).</summary>
    protected static readonly ClaimsPrincipal Anonymous = new(new ClaimsIdentity());

    private readonly MutableAuthenticationStateProvider _authProvider = new(Anonymous);

    protected BunitComponentTestBase()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddAuthorizationCore();
        Services.AddSingleton<IAuthorizationService, IsAuthenticatedAuthorizationService>();
        Services.AddSingleton<AuthenticationStateProvider>(_authProvider);
    }

    /// <summary>Sets the principal the injected <see cref="AuthenticationStateProvider"/> returns (and notifies listeners) without re-rendering a new root — useful for mid-test auth changes.</summary>
    protected void SetUser(ClaimsPrincipal principal) => _authProvider.SetPrincipal(principal);

    /// <summary>Renders <typeparamref name="TComponent"/> as the anonymous user.</summary>
    protected IRenderedComponent<TComponent> RenderUnderTest<TComponent>(
        Action<ComponentParameterCollectionBuilder<TComponent>> parameters)
        where TComponent : IComponent
        => RenderAs(Anonymous, parameters);

    /// <summary>Renders <typeparamref name="TComponent"/> as <paramref name="principal"/>, driving both the cascading <c>AuthenticationState</c> and the injected provider.</summary>
    protected IRenderedComponent<TComponent> RenderAs<TComponent>(
        ClaimsPrincipal principal,
        Action<ComponentParameterCollectionBuilder<TComponent>> parameters)
        where TComponent : IComponent
    {
        _authProvider.SetPrincipal(principal);
        return Render<TComponent>(p =>
        {
            p.AddCascadingValue(Task.FromResult(new AuthenticationState(principal)));
            parameters(p);
        });
    }

    /// <summary>
    /// Renders MudBlazor's popover, dialog, and snackbar providers into the test's render root so
    /// components that open a <c>MudMessageBox</c>/dialog or raise a snackbar have somewhere to render.
    /// Render this BEFORE the component under test, then click into the returned providers' markup.
    /// </summary>
    protected MudProviderHandles RenderMudProviders()
    {
        var popover = Render<MudPopoverProvider>();
        var dialog = Render<MudDialogProvider>();
        var snackbar = Render<MudSnackbarProvider>();
        return new MudProviderHandles(popover, dialog, snackbar);
    }

    /// <summary>Handles to the rendered MudBlazor providers (query <see cref="Dialog"/> for message-box/dialog markup, <see cref="Snackbar"/> for toasts).</summary>
    protected sealed record MudProviderHandles(
        IRenderedComponent<MudPopoverProvider> Popover,
        IRenderedComponent<MudDialogProvider> Dialog,
        IRenderedComponent<MudSnackbarProvider> Snackbar);

    private sealed class MutableAuthenticationStateProvider(ClaimsPrincipal initial) : AuthenticationStateProvider
    {
        private ClaimsPrincipal _principal = initial;

        public void SetPrincipal(ClaimsPrincipal principal)
        {
            _principal = principal;
            NotifyAuthenticationStateChanged(Task.FromResult(new AuthenticationState(principal)));
        }

        public override Task<AuthenticationState> GetAuthenticationStateAsync()
            => Task.FromResult(new AuthenticationState(_principal));
    }

    private sealed class IsAuthenticatedAuthorizationService : IAuthorizationService
    {
        public Task<AuthorizationResult> AuthorizeAsync(
            ClaimsPrincipal user, object? resource, IEnumerable<IAuthorizationRequirement> requirements)
            => Task.FromResult(user.Identity?.IsAuthenticated == true
                ? AuthorizationResult.Success()
                : AuthorizationResult.Failed());

        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, string policyName)
            => AuthorizeAsync(user, resource, []);
    }
}

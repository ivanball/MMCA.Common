using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Localization;
using Microsoft.JSInterop;
using MMCA.Common.Shared.Auth;
using MMCA.Common.Shared.Auth.Permissions;
using MMCA.Common.UI.Resources;

namespace MMCA.Common.UI.Components.Auth;

/// <summary>
/// The "View as" control for an app's administrator user page (ADR-137): asks for a reason, then
/// starts a read-only, 15-minute view of the app as <see cref="UserId"/>. Shown only to a principal
/// holding <c>users:view-as</c> that is not itself viewing as someone.
/// <para>
/// <b>Web only, through the same-origin proxy.</b> The grant is posted from the browser to
/// <c>{ApiPathPrefix}/{UsersRoute}/{UserId}/view-as</c>, so the proxy keeps the token in its HttpOnly
/// overlay cookie and the page never sees it; on success the app reloads as the viewed user. MAUI
/// does not offer view-as.
/// </para>
/// </summary>
public partial class ViewAsButton
{
    private bool _canViewAs;
    private bool _open;
    private bool _starting;
    private string? _reason;
    private string? _error;

    /// <summary>The account to view.</summary>
    [Parameter]
    [EditorRequired]
    public UserIdentifierType UserId { get; set; }

    /// <summary>The same-origin proxy's path prefix (default <c>/api</c>).</summary>
    [Parameter]
    public string? ApiPathPrefix { get; set; }

    /// <summary>
    /// The route the app gives its <c>UsersAdminControllerBase</c> subclass, relative to the gateway
    /// root (default <c>Admin/Users</c>).
    /// </summary>
    [Parameter]
    public string? UsersRoute { get; set; }

    [CascadingParameter] private Task<AuthenticationState>? AuthenticationState { get; set; }

    [Inject] private IStringLocalizer<SharedResource> L { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;

    private bool ReasonIsValid => _reason?.Trim().Length is >= 10 and <= 500;

    /// <inheritdoc />
    protected override async Task OnParametersSetAsync()
    {
        var user = AuthenticationState is null ? null : (await AuthenticationState).User;
        _canViewAs = user.HasPermissionClaim(AdministrationPermissions.ViewAsUsers) && !user.IsViewAs();
    }

    private void Open()
    {
        _open = true;
        _error = null;
    }

    private void Cancel()
    {
        _open = false;
        _reason = null;
        _error = null;
    }

    private async Task StartAsync()
    {
        if (!ReasonIsValid || _starting)
        {
            return;
        }

        _starting = true;
        _error = null;
        try
        {
            var url = string.Create(
                CultureInfo.InvariantCulture,
                $"{(ApiPathPrefix ?? "/api").TrimEnd('/')}/{(UsersRoute ?? "Admin/Users").Trim('/')}/{UserId}/view-as");

            // The script reloads the app on success, so only a refusal comes back here.
            var status = await JS.InvokeAsync<int>("mmcaViewAs.start", url, _reason!.Trim());
            if (status is < 200 or >= 300)
            {
                _error = L["Auth.ViewAs.Refused"].Value;
            }
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or InvalidOperationException or TaskCanceledException)
        {
            _error = L["Auth.ViewAs.Refused"].Value;
        }
        finally
        {
            _starting = false;
        }
    }
}

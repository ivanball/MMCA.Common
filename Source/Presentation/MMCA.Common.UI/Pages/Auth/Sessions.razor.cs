using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth;
using MMCA.Common.UI.Common;
using MMCA.Common.UI.Resources;
using MMCA.Common.UI.Services.Auth;
using MudBlazor;

namespace MMCA.Common.UI.Pages.Auth;

/// <summary>
/// Code-behind for the signed-in devices page: one row per live refresh session, with a per-device
/// sign-out and a sign-out-everywhere.
/// <para>
/// <b>Two revoke paths, on purpose.</b> A row's button calls <c>auth/revoke/{sessionId}</c>, which
/// ends one other device's session and leaves this one alone. The page-level button is the
/// account-wide <c>auth/revoke</c>, which also ends the session the caller is using, so it is
/// followed by the normal local sign-out (<see cref="IAuthUIService.LogoutAsync"/> does both) and a
/// redirect to the login page. The row for the current device therefore offers no button at all:
/// revoking it from here would leave the app signed in on a dead session until the access token
/// expired.
/// </para>
/// </summary>
public partial class Sessions : IDisposable
{
    [Inject] private IAuthUIService AuthService { get; set; } = default!;
    [Inject] private NavigationManager Navigation { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;
    [Inject] private IStringLocalizer<SharedResource> L { get; set; } = default!;

    private const string LoginRoute = "/login";

    private readonly CancellationTokenSource _cts = new();

    private List<BreadcrumbItem> _breadcrumbs = [];
    private IReadOnlyList<RefreshSessionSummaryResponse> _sessions = [];

    /// <summary>The last load attempt's outcome; a failure is rendered inline with a retry.</summary>
    private Result? _loadResult;

    private Guid? _revokingSessionId;
    private bool _disposed;

    private string Title => L["Auth.Sessions.Title"].Value;

    /// <summary>True while the initial (or retried) load is in flight.</summary>
    protected bool IsLoading { get; private set; }

    /// <summary>
    /// True while any revoke is in flight. Every button reads it, so a second click cannot start a
    /// concurrent revoke while the list is about to be rebuilt underneath it.
    /// </summary>
    protected bool IsBusy => _revokingSessionId is not null || IsRevokingAll;

    /// <summary>True while the account-wide sign-out is in flight.</summary>
    private bool IsRevokingAll { get; set; }

    protected override async Task OnInitializedAsync()
    {
        // Built here (not in a field initializer) so the injected localizer is available (ADR-027).
        _breadcrumbs =
        [
            new(L["Breadcrumb.Home"].Value, RoutePaths.Home, icon: Icons.Material.Filled.Home),
            new(L["Auth.Sessions.Title"].Value, href: null, disabled: true),
        ];

        await LoadSessionsAsync();
    }

    private async Task LoadSessionsAsync()
    {
        IsLoading = true;
        _loadResult = null;

        try
        {
            var result = await AuthService.GetSessionsAsync(_cts.Token);
            _loadResult = result;

            if (result.TryGetValue(out var sessions))
            {
                _sessions = sessions;
            }
            else
            {
                // The list is emptied deliberately: a failed read must not leave a stale device list
                // on screen that a user could act on.
                _sessions = [];
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during component disposal.
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Signs one other device out, then reloads the list from the server rather than removing the
    /// row locally: the server is the authority on what is still live, and a reload also catches a
    /// session that expired while the page was open.
    /// </summary>
    private async Task RevokeSessionAsync(RefreshSessionSummaryResponse session)
    {
        if (IsBusy || session.IsCurrent)
        {
            return;
        }

        _revokingSessionId = session.SessionId;

        try
        {
            var result = await AuthService.RevokeSessionAsync(session.SessionId, _cts.Token);

            if (result.IsSuccess)
            {
                Snackbar.Add(L["Auth.Sessions.Revoked"], Severity.Success);
            }
            else if (result.IsNotFound())
            {
                // Already gone (a duplicate click, or the device signed itself out): the user's
                // intent is satisfied, so this is not an error to shout about.
                Snackbar.Add(L["Auth.Sessions.AlreadyRevoked"], Severity.Info);
            }
            else
            {
                result.NotifyOnFailure(Snackbar, L);
                return;
            }

            await LoadSessionsAsync();
        }
        catch (OperationCanceledException)
        {
            // Expected during component disposal.
        }
        finally
        {
            _revokingSessionId = null;
        }
    }

    /// <summary>
    /// Ends every session, including this one. <see cref="IAuthUIService.LogoutAsync"/> is exactly
    /// that operation: it calls the account-wide revoke and then clears local token storage and
    /// notifies auth state, which is what keeps the app from sitting on a revoked session.
    /// </summary>
    private async Task RevokeAllAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsRevokingAll = true;

        try
        {
            await AuthService.LogoutAsync();
            Navigation.NavigateTo(LoginRoute, forceLoad: true);
        }
        finally
        {
            IsRevokingAll = false;
        }
    }

    /// <summary>
    /// The device label: browser and platform read out of the user agent, composed through a
    /// resource format so the word order translates (ADR-027), and an explicit "unknown device"
    /// when the header identified neither.
    /// </summary>
    private string DescribeDevice(RefreshSessionSummaryResponse session)
    {
        var (browser, platform) = UserAgentSummary.Parse(session.UserAgent);

        return (browser, platform) switch
        {
            (not null, not null) => L["Auth.Sessions.Device.Format", browser, platform].Value,
            (not null, null) => browser,
            (null, not null) => platform,
            _ => L["Auth.Sessions.Device.Unknown"].Value,
        };
    }

    /// <summary>
    /// Formats a UTC instant in the viewer's local time and current culture: the sessions endpoint
    /// reports UTC, and "signed in at 03:14" is only meaningful on the clock the person reads.
    /// </summary>
    private static string FormatInstant(DateTime instant) =>
        DateTime.SpecifyKind(instant, DateTimeKind.Utc).ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture);

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        if (disposing)
        {
            _cts.Cancel();
            _cts.Dispose();
        }

        _disposed = true;
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }
}

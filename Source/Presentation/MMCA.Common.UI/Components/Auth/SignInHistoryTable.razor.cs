using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using MMCA.Common.Shared.Auth.Responses;
using MMCA.Common.UI.Common;
using MMCA.Common.UI.Resources;
using MMCA.Common.UI.Services.Auth.Devices;
using MMCA.Common.UI.Services.Culture;

namespace MMCA.Common.UI.Components.Auth;

/// <summary>
/// A read-only sign-in history table: one row per sign-in (device, IP address, signed in, last
/// active, and how it ended), with an empty state. Used by the signed-in devices page for the
/// caller's own history and by an app's administrator user page for another account's.
/// <para>
/// The device label and the viewer-clock instants match <see cref="UserSessionsTable"/>
/// (<see cref="UserAgentSummary.Describe"/>, <see cref="ViewerTimeZone"/>). The status cell reads
/// <see cref="SignInSummaryResponse.EndReason"/> through <see cref="SignInEndReasons"/>; a reason this
/// version does not know renders as a plain "ended" rather than the raw value.
/// </para>
/// </summary>
public partial class SignInHistoryTable : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    /// <summary>The sign-ins to show, newest first.</summary>
    [Parameter]
    [EditorRequired]
    public IReadOnlyList<SignInSummaryResponse> SignIns { get; set; } = [];

    [Inject] private IStringLocalizer<SharedResource> L { get; set; } = default!;
    [Inject] private ViewerTimeZone ViewerTime { get; set; } = default!;

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        // Sign-in times render on the viewer's clock; the zone is only readable once JS is available.
        if (firstRender && !_disposed && await ViewerTime.EnsureResolvedAsync(_cts.LifetimeToken()))
        {
            StateHasChanged();
        }
    }

    /// <summary>Releases the cancellation source that bounds the time-zone lookup.</summary>
    /// <param name="disposing"><see langword="true"/> when called from <see cref="Dispose()"/>.</param>
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

    private string DescribeDevice(SignInSummaryResponse signIn) => UserAgentSummary.Describe(signIn.UserAgent, L);

    private string DescribeIpAddress(SignInSummaryResponse signIn) =>
        string.IsNullOrWhiteSpace(signIn.IpAddress) ? L["Auth.Sessions.IpUnknown"].Value : signIn.IpAddress;

    private string DescribeStatus(SignInSummaryResponse signIn) => signIn.EndReason switch
    {
        null when signIn.EndedAt is null => L["Auth.SignIns.Status.Active"].Value,
        SignInEndReasons.SignedOut => L["Auth.SignIns.Status.SignedOut"].Value,
        SignInEndReasons.ReuseDetected => L["Auth.SignIns.Status.ReuseDetected"].Value,
        SignInEndReasons.SessionCapExceeded => L["Auth.SignIns.Status.SessionCapExceeded"].Value,
        SignInEndReasons.AccountDeleted => L["Auth.SignIns.Status.AccountDeleted"].Value,
        SignInEndReasons.Expired => L["Auth.SignIns.Status.Expired"].Value,
        _ => L["Auth.SignIns.Status.Ended"].Value,
    };

    private string FormatInstant(DateTime instant) => ViewerTime.Format(instant);
}

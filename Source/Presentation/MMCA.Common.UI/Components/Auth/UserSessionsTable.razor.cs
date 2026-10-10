using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using MMCA.Common.Shared.Auth.Responses;
using MMCA.Common.UI.Common;
using MMCA.Common.UI.Resources;
using MMCA.Common.UI.Services.Auth.Devices;
using MMCA.Common.UI.Services.Culture;

namespace MMCA.Common.UI.Components.Auth;

/// <summary>
/// A read-only table of one account's signed-in devices (live refresh sessions) for an administrator:
/// device, IP address, signed in and expires, with an empty state. It offers no sign-out.
/// <para>
/// The device label is the one the signed-in devices page shows (<see cref="UserAgentSummary.Describe"/>),
/// and the instants render on the VIEWER's clock through <see cref="ViewerTimeZone"/>, exactly as on
/// that page. No row is ever marked as the current device: an administrator is never on the viewed
/// user's device.
/// </para>
/// </summary>
public partial class UserSessionsTable : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    /// <summary>The live sessions to show, newest first.</summary>
    [Parameter]
    [EditorRequired]
    public IReadOnlyList<RefreshSessionSummaryResponse> Sessions { get; set; } = [];

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
        // Session times render on the viewer's clock; the zone is only readable once JS is available.
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

    private string DescribeDevice(RefreshSessionSummaryResponse session) => UserAgentSummary.Describe(session.UserAgent, L);

    private string DescribeIpAddress(RefreshSessionSummaryResponse session) =>
        string.IsNullOrWhiteSpace(session.IpAddress) ? L["Auth.Sessions.IpUnknown"].Value : session.IpAddress;

    private string FormatInstant(DateTime instant) => ViewerTime.Format(instant);
}

using Microsoft.AspNetCore.Components;
using MMCA.Common.Shared.Auth.Responses;

namespace MMCA.Common.UI.Components.Auth;

/// <summary>
/// A read-only table of one account's signed-in devices (live refresh sessions) for an administrator:
/// device, IP address, signed in and expires, with an empty state. It offers no sign-out.
/// </summary>
public partial class UserSessionsTable
{
    /// <summary>The live sessions to show, newest first.</summary>
    [Parameter]
    [EditorRequired]
    public IReadOnlyList<RefreshSessionSummaryResponse> Sessions { get; set; } = [];
}

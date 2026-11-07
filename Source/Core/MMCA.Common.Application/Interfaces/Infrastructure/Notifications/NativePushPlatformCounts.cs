using System.Runtime.InteropServices;

namespace MMCA.Common.Application.Interfaces.Infrastructure.Notifications;

/// <summary>One platform's counts in a <see cref="NativePushOutcome"/>.</summary>
/// <param name="Sent">Sends the provider accepted.</param>
/// <param name="Failed">Sends the provider refused or that never reached it.</param>
[StructLayout(LayoutKind.Auto)]
public readonly record struct NativePushPlatformCounts(int Sent, int Failed)
{
    /// <summary>Gets the number of sends attempted (<see cref="Sent"/> plus <see cref="Failed"/>).</summary>
    public int Attempted => Sent + Failed;
}

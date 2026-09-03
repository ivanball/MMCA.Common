using MMCA.Common.Testing.Architecture;

namespace MMCA.Common.Architecture.Tests.Cqrs;

/// <summary>
/// Trailing-CancellationToken convention for the MMCA.Common Application and Infrastructure packages,
/// driven by the shared rule library (<see cref="CancellationTokenConventionTestsBase"/>) over
/// <see cref="CommonArchitectureMap"/>.
/// </summary>
public sealed class CancellationTokenConventionTests : CancellationTokenConventionTestsBase
{
    protected override IArchitectureMap Map { get; } = new CommonArchitectureMap();

    /// <summary>
    /// The two SignalR hub methods on <c>NotificationHub</c>. A hub method signature is not an ordinary
    /// public API: it IS the client-visible RPC contract, bound by name and argument list by SignalR's
    /// dispatcher, and every shipped consumer's JavaScript/Blazor client already invokes
    /// <c>JoinChannel</c>/<c>LeaveChannel</c> with exactly one argument. Adding a parameter would change
    /// that wire contract for a token the hub already has: both methods pass
    /// <c>Context.ConnectionAborted</c> straight into the group calls, so the work IS cancellable, just
    /// not through a parameter the rule can see.
    /// </summary>
    protected override IReadOnlyList<string> CancellationTokenExemptMethods =>
    [
        "NotificationHub.JoinChannelAsync",
        "NotificationHub.LeaveChannelAsync",
    ];
}

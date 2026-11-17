namespace MMCA.Common.Testing.Architecture;

/// <summary>
/// Fitness function: every public async method on a public Application- or Infrastructure-layer type
/// declares a trailing <c>CancellationToken cancellationToken</c>. Cancellation is only end-to-end if it
/// is uniform: one method that swallows the token turns a cancelled request, an expired CQRS timeout
/// budget or a stopping host into work that keeps running against the database, and a token in a
/// non-trailing position or under a different name defeats the mechanical forwarding the decorator
/// pipeline and the repositories rely on.
/// <para>
/// Signatures the repo does not own are exempt automatically (overrides and interface implementations
/// from outside the map's assemblies, <c>Dispose</c>/<c>DisposeAsync</c>, compiler-generated members);
/// see <see cref="ArchitectureRules.AsyncMethodsDeclareTrailingCancellationToken(IArchitectureMap, IReadOnlyCollection{string})"/>.
/// </para>
/// <para>
/// The framework's own exemptions (<see cref="FrameworkExemptMethods"/>) are always applied, so a
/// consumer whose map reaches MMCA.Common's layers inherits them instead of copying them.
/// </para>
/// </summary>
public abstract class CancellationTokenConventionTestsBase
{
    /// <summary>
    /// The framework methods every subclass exempts: the two SignalR hub methods on MMCA.Common's
    /// <c>NotificationHub</c>. A hub method signature is not an ordinary public API: it IS the
    /// client-visible RPC contract, bound by name and argument list by SignalR's dispatcher, and every
    /// shipped consumer's JavaScript/Blazor client invokes <c>JoinChannel</c>/<c>LeaveChannel</c> with
    /// exactly one argument. Adding a parameter would change that wire contract for a token the hub
    /// already has: both methods pass <c>Context.ConnectionAborted</c> straight into the group calls, so
    /// the work IS cancellable, just not through a parameter the rule can see.
    /// </summary>
    private static readonly string[] FrameworkExemptMethods =
    [
        "NotificationHub.JoinChannelAsync",
        "NotificationHub.LeaveChannelAsync",
    ];

    /// <summary>The repo's architecture map.</summary>
    protected abstract IArchitectureMap Map { get; }

    /// <summary>
    /// Additional exemptions in <c>"TypeName.MethodName"</c> form, applied on top of the framework's own
    /// (which every subclass inherits). Reserve these for a shipped public API where adding the parameter
    /// would break consumers, and justify each entry with a comment; a method that simply has not been
    /// updated yet should be fixed instead. Empty by default.
    /// </summary>
    protected virtual IReadOnlyList<string> CancellationTokenExemptMethods => [];

    [Fact]
    public void AsyncMethods_ShouldDeclare_TrailingCancellationToken() =>
        ArchitectureRules.AsyncMethodsDeclareTrailingCancellationToken(Map, [.. FrameworkExemptMethods, .. CancellationTokenExemptMethods]);
}

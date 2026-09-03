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
/// </summary>
public abstract class CancellationTokenConventionTestsBase
{
    /// <summary>The repo's architecture map.</summary>
    protected abstract IArchitectureMap Map { get; }

    /// <summary>
    /// Additional exemptions in <c>"TypeName.MethodName"</c> form. Reserve these for a shipped public API
    /// where adding the parameter would break consumers, and justify each entry with a comment; a method
    /// that simply has not been updated yet should be fixed instead. Empty by default.
    /// </summary>
    protected virtual IReadOnlyList<string> CancellationTokenExemptMethods => [];

    [Fact]
    public void AsyncMethods_ShouldDeclare_TrailingCancellationToken() =>
        ArchitectureRules.AsyncMethodsDeclareTrailingCancellationToken(Map, CancellationTokenExemptMethods);
}

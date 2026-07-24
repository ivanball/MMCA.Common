namespace MMCA.Common.API.Authorization;

/// <summary>
/// Marks an action (or an entire controller) as exempt from <see cref="OwnerOrAdminFilter"/>'s
/// requirement that the request carry a resolvable owner identifier.
/// <para>
/// The filter denies by default: an action it guards whose owner parameter is absent or
/// unparseable is rejected, because "no owner to compare" must not read as "no restriction". Some
/// actions legitimately have no owner parameter and are guarded another way, typically a
/// collection endpoint whose rows are already narrowed to the caller by an ownership
/// specification, or one restricted to administrators by its own authorization policy. Those
/// actions carry this attribute.
/// </para>
/// </summary>
/// <remarks>
/// Applying this attribute is an assertion that the action is guarded elsewhere. Always state
/// which guard replaces the parameter check in a comment at the application site, so a later
/// reader can verify the claim instead of trusting it.
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class AllowMissingOwnerAttribute : Attribute
{
}

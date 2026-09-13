namespace MMCA.Common.Shared.Auth.Permissions;

/// <summary>
/// The compiled universe an administration surface may offer: every role the host's registry knows
/// about, and every permission its code is able to grant.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it is separate from <see cref="IPermissionRegistry"/>.</b> The registry answers questions
/// ABOUT a role; it deliberately cannot enumerate. An editor screen needs the opposite: the closed
/// list of checkboxes it may render. Splitting the two keeps the hot authorization path a pair of
/// frozen-set lookups while still giving the administration surface something to draw.
/// </para>
/// <para>
/// <b>It is the compiled universe, never the stored one.</b> A stored grant can only pick a
/// permission out of this list, which is what stops an operator from inventing a capability no
/// endpoint checks, and what makes a typo a rejection rather than a silently inert row.
/// </para>
/// <para>
/// Both collections are sorted ordinally and carry no duplicates, so a caller can render them
/// directly and compare them by position.
/// </para>
/// </remarks>
public interface IPermissionCatalog
{
    /// <summary>Gets every role the compiled registry grants something to, sorted ordinally.</summary>
    IReadOnlyList<string> Roles { get; }

    /// <summary>Gets every permission the compiled registry grants to anyone, sorted ordinally.</summary>
    IReadOnlyList<string> Permissions { get; }
}

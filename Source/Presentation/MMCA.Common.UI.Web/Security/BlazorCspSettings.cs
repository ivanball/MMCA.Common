namespace MMCA.Common.UI.Web.Security;

/// <summary>
/// Strongly-typed options bound to the <c>"BlazorCsp"</c> configuration section: the host-specific
/// allowances the Blazor Content-Security-Policy (registered by <c>AddCommonBlazorCsp()</c>) adds on
/// top of its hardened baseline.
/// </summary>
/// <remarks>
/// Example (<c>appsettings.json</c>):
/// <code>
/// "BlazorCsp": {
///   "FrameSources": [ "https://www.google.com", "https://maps.google.com" ]
/// }
/// </code>
/// Every entry is validated at startup (<c>ValidateOnStart</c>); an invalid entry fails the boot with a
/// message naming it, rather than emitting a policy that is either broken or wider than intended.
/// </remarks>
public sealed class BlazorCspSettings
{
    /// <summary>Configuration section name used for binding.</summary>
    public const string SectionName = "BlazorCsp";

    /// <summary>
    /// Origins the host's pages may embed in an <c>&lt;iframe&gt;</c> (for example a map provider's
    /// embed endpoint). When the list is empty (the default) the policy carries no <c>frame-src</c>
    /// directive, so frames fall back to <c>default-src 'self'</c>. When it is non-empty the policy
    /// emits <c>frame-src 'self' &lt;origins&gt;</c>.
    /// <para>
    /// Each entry must be an absolute <c>https</c> origin (<c>https://host</c> or
    /// <c>https://host:port</c>, an optional trailing slash is tolerated) with no path, query,
    /// fragment, user info, wildcard, quote, semicolon, comma or whitespace. This only governs what
    /// this host may frame; <c>frame-ancestors 'none'</c> (who may frame this host) is unaffected.
    /// </para>
    /// </summary>
    public IList<string> FrameSources { get; } = [];
}

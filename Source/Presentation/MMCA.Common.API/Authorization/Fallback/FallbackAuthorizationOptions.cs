namespace MMCA.Common.API.Authorization.Fallback;

/// <summary>
/// Settings for the framework's fallback authorization policy: the rule that an endpoint carrying
/// no authorization metadata at all requires an authenticated caller.
/// <para>
/// SECURITY: without a fallback policy, forgetting <c>[Authorize]</c> on a controller publishes
/// every action on it to anonymous callers, and no fitness test can tell that omission from a
/// deliberate public endpoint. With it, anonymity has to be declared (<c>[AllowAnonymous]</c>,
/// <c>.AllowAnonymous()</c>, or an entry in <see cref="ExemptPathPrefixes"/>), so the mistake fails
/// closed instead of open.
/// </para>
/// </summary>
public sealed class FallbackAuthorizationOptions
{
    /// <summary>
    /// The path prefixes exempt from the fallback policy out of the box: framework and static
    /// surfaces that are endpoint-routed but carry no authorization metadata of their own (Blazor's
    /// framework files and circuit, static asset conventions, health probes, well-known documents).
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultExemptPathPrefixes =
    [
        "/_framework",
        "/_content",
        "/_blazor",
        "/_vs",
        "/.well-known",
        "/health",
        "/alive",
        "/css",
        "/js",
        "/lib",
        "/images",
        "/img",
        "/fonts",
        "/media",
        "/favicon.ico",
        "/robots.txt",
        "/sitemap.xml",
        "/manifest.json",
        "/site.webmanifest",
        "/service-worker.js",
        "/apple-app-site-association",
    ];

    /// <summary>
    /// Whether the fallback policy is applied. Defaults to <see langword="true"/>: a host opts out
    /// deliberately (<c>AddAuthorizationPolicies(options =&gt; options.Enabled = false)</c>) rather
    /// than by omission, and the opt-out is the documented escape hatch for a host that cannot
    /// annotate its anonymous surface yet.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Request path prefixes the fallback policy never gates, seeded with
    /// <see cref="DefaultExemptPathPrefixes"/>. Matching is segment-based and case-insensitive, so
    /// <c>/_framework</c> covers <c>/_framework/blazor.web.js</c>. Add a host's own static roots
    /// here; do NOT add application endpoints, which should carry <c>[AllowAnonymous]</c> so the
    /// anonymous-endpoint fitness gate can see them.
    /// </summary>
    public IList<string> ExemptPathPrefixes { get; } = [.. DefaultExemptPathPrefixes];
}

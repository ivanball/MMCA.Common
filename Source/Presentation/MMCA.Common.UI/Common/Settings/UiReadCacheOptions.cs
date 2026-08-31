namespace MMCA.Common.UI.Common.Settings;

/// <summary>
/// Strongly-typed options bound to the <c>"UiReadCache"</c> configuration section: the explicit
/// client-side staleness policy for <see cref="MMCA.Common.UI.Services.Caching.IUiReadCache"/>.
/// <para>
/// The point of stating it in configuration is that staleness becomes a decision a host records
/// rather than an accident of how often a component happens to re-render. A host that wants every
/// read to hit the API sets <see cref="Enabled"/> to <see langword="false"/>; a host with reference
/// data that changes hourly gives that route prefix its own, much longer, TTL.
/// </para>
/// </summary>
public sealed class UiReadCacheOptions
{
    /// <summary>Configuration section name used for binding.</summary>
    public static readonly string SectionName = "UiReadCache";

    /// <summary>
    /// Whether the read cache serves and stores anything. <see langword="false"/> turns every lookup
    /// into a miss and every store into a no-op, so the services behave exactly as they did before a
    /// cache was registered (the framework's own escape hatch for a host that wants no client-side
    /// staleness at all).
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Freshness budget applied to any read whose URL matches no entry in
    /// <see cref="RoutePrefixTtls"/>. Sixty seconds is short enough that a stale list corrects itself
    /// within one user's attention span, and long enough to collapse the burst of identical reads a
    /// page issues while it mounts.
    /// </summary>
    public TimeSpan DefaultTtl { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Per-route-prefix TTL overrides, keyed by the leading part of the relative URL (e.g.
    /// <c>countries</c>). The LONGEST matching prefix wins, so a specific child route can state a
    /// different budget than the endpoint it sits under. Getter-only so the configuration binder
    /// populates the instance the defaults created, which is how the other settings classes in this
    /// namespace shape their bindable collections.
    /// </summary>
    public Dictionary<string, TimeSpan> RoutePrefixTtls { get; } = [];
}

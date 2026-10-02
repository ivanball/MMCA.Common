// Reads the viewer's IANA time zone (for example "America/New_York") so server UTC instants can be
// shown on the clock the person actually reads. Returns null when the browser cannot say, and the
// caller then falls back to UTC. Wrapped in try/catch like the other modules in this folder, so an
// old or locked-down browser degrades instead of breaking the calling component.

export function getTimeZone() {
    try {
        const zone = Intl.DateTimeFormat().resolvedOptions().timeZone;
        return typeof zone === 'string' && zone.length > 0 ? zone : null;
    } catch {
        return null;
    }
}

using System.Globalization;
using Microsoft.JSInterop;

namespace MMCA.Common.UI.Services.Culture;

/// <summary>
/// Shows server UTC instants on the VIEWER's clock. The browser's IANA time zone is read once per
/// scope through <c>time-zone.js</c> (<c>Intl.DateTimeFormat().resolvedOptions().timeZone</c>) and
/// resolved with <see cref="TimeZoneInfo"/>; until then, and whenever it cannot be read (SSR
/// prerender, a host without JS, an id this runtime does not know), every conversion uses UTC.
/// </summary>
/// <remarks>
/// The server's own zone is never used: on Blazor Server and during prerender
/// <see cref="DateTime.ToLocalTime"/> is the server's clock, which is the wrong answer for every
/// viewer elsewhere. A page calls <see cref="EnsureResolvedAsync"/> from <c>OnAfterRenderAsync</c>
/// on its first render and re-renders when it returns <see langword="true"/>.
/// </remarks>
public sealed class ViewerTimeZone : IAsyncDisposable
{
    private const string ModulePath = "./_content/MMCA.Common.UI/time-zone.js";

    private readonly LazyJsModule _module;

    /// <summary>Initializes the service over the host's <see cref="IJSRuntime"/>.</summary>
    /// <param name="jsRuntime">The JS runtime that reads the browser's time zone.</param>
    public ViewerTimeZone(IJSRuntime jsRuntime) => _module = new LazyJsModule(jsRuntime, ModulePath);

    /// <summary>
    /// Gets the zone instants are converted to: the viewer's zone once resolved, otherwise
    /// <see cref="TimeZoneInfo.Utc"/>.
    /// </summary>
    public TimeZoneInfo Zone { get; private set; } = TimeZoneInfo.Utc;

    /// <summary>
    /// Gets a value indicating whether the browser has been asked for its zone (successfully or not).
    /// Stays <see langword="false"/> while JS interop is unavailable, so a later call can retry.
    /// </summary>
    public bool IsResolved { get; private set; }

    /// <summary>
    /// Reads the viewer's time zone from the browser, once. Safe during prerender: the call is
    /// skipped and the zone stays UTC until a later call can reach the browser.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <see langword="true"/> when this call changed <see cref="Zone"/>, so the caller should
    /// re-render; otherwise <see langword="false"/>.
    /// </returns>
    public async ValueTask<bool> EnsureResolvedAsync(CancellationToken cancellationToken = default)
    {
        if (IsResolved)
        {
            return false;
        }

        string? zoneId;
        try
        {
            var module = await _module.GetOrImportAsync(cancellationToken).ConfigureAwait(false);
            zoneId = await module.InvokeAsync<string?>("getTimeZone", cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // JS interop is not available yet (SSR prerender): keep UTC and let a later call retry.
            return false;
        }
        catch (JSDisconnectedException)
        {
            return false;
        }
        catch (OperationCanceledException)
        {
            // The page went away mid-call: nothing to render, and the zone can still be read later.
            return false;
        }
        catch (JSException)
        {
            // The browser answered with an error: it will not do better on a retry.
            zoneId = null;
        }

        IsResolved = true;
        var resolved = ResolveZone(zoneId);
        var changed = !string.Equals(resolved.Id, Zone.Id, StringComparison.Ordinal);
        Zone = resolved;
        return changed;
    }

    /// <summary>
    /// Converts a UTC instant to the viewer's zone. <see cref="DateTimeKind.Utc"/> and
    /// <see cref="DateTimeKind.Unspecified"/> are both read as UTC (the API serializes UTC instants,
    /// and a deserialized value often arrives unspecified); a <see cref="DateTimeKind.Local"/> value
    /// is first normalized to UTC.
    /// </summary>
    /// <param name="instant">The instant to convert.</param>
    /// <returns>The same instant on the viewer's clock.</returns>
    public DateTime ToViewerTime(DateTime instant)
    {
        var utc = instant.Kind == DateTimeKind.Local
            ? instant.ToUniversalTime()
            : DateTime.SpecifyKind(instant, DateTimeKind.Utc);

        return TimeZoneInfo.ConvertTimeFromUtc(utc, Zone);
    }

    /// <summary>
    /// Formats a UTC instant on the viewer's clock with the current culture.
    /// </summary>
    /// <param name="instant">The instant to format (UTC or unspecified, read as UTC).</param>
    /// <param name="format">A standard or custom date and time format string; <c>"g"</c> by default.</param>
    /// <returns>The formatted local time.</returns>
    public string Format(DateTime instant, string format = "g") =>
        ToViewerTime(instant).ToString(format, CultureInfo.CurrentCulture);

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _module.DisposeAsync();

    /// <summary>
    /// Resolves an IANA (or Windows) zone id to a <see cref="TimeZoneInfo"/>, falling back to UTC
    /// for a missing id or one this runtime does not know.
    /// </summary>
    private static TimeZoneInfo ResolveZone(string? zoneId) =>
        !string.IsNullOrWhiteSpace(zoneId) && TimeZoneInfo.TryFindSystemTimeZoneById(zoneId, out var zone)
            ? zone
            : TimeZoneInfo.Utc;
}

using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Playwright;

namespace MMCA.Common.Testing.E2E.Infrastructure;

/// <summary>
/// Captures client-side Core Web Vitals (LCP, CLS, FCP, TTFB) plus a single-interaction INP latency
/// sample from a live page using the browser <c>PerformanceObserver</c> / Navigation Timing APIs
/// (rubric §12). No third-party JS or network egress: an init script installs the observers before
/// first paint and accumulates into <c>window.__vitals</c>, which <see cref="CollectAsync"/> reads back.
/// LCP/CLS are Chromium-only metrics, so on Firefox/WebKit those fields stay 0 (the observers fail
/// silently and the budget assertions pass) — this is the client-side analogue of a backend k6 load
/// test, not a cross-engine field measurement. Consumers keep their own budget-asserting tests; this
/// class is only the measurement infrastructure.
/// </summary>
public static class WebVitalsCollector
{
    // Installed via AddInitScript so the observers exist before the document's own scripts run. Each
    // observer is wrapped in try/catch so an engine that lacks the entry type (LCP/CLS on Firefox/WebKit)
    // leaves that metric at 0 rather than throwing. Kept as one concatenated string (no multi-line raw
    // literal) to stay clear of MA0136.
    private const string InitScript =
        "window.__vitals = { lcp: 0, cls: 0, fcp: 0, ttfb: 0, inp: 0 };" +
        "try { new PerformanceObserver((l) => { const es = l.getEntries(); const last = es[es.length - 1];" +
        " if (last) { window.__vitals.lcp = last.startTime; } }).observe({ type: 'largest-contentful-paint', buffered: true }); } catch (e) { }" +
        "try { new PerformanceObserver((l) => { for (const en of l.getEntries()) {" +
        " if (!en.hadRecentInput) { window.__vitals.cls += en.value; } } }).observe({ type: 'layout-shift', buffered: true }); } catch (e) { }" +
        "try { new PerformanceObserver((l) => { for (const en of l.getEntries()) {" +
        " if (en.name === 'first-contentful-paint') { window.__vitals.fcp = en.startTime; } } }).observe({ type: 'paint', buffered: true }); } catch (e) { }" +
        "try { new PerformanceObserver((l) => { for (const en of l.getEntries()) {" +
        " if (en.duration > window.__vitals.inp) { window.__vitals.inp = en.duration; } } }).observe({ type: 'event', buffered: true, durationThreshold: 16 }); } catch (e) { }";

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    /// <summary>Registers the Web-Vitals observers so they are active on the next navigation.</summary>
    public static Task InstallAsync(IPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        return page.AddInitScriptAsync(InitScript);
    }

    /// <summary>Reads the accumulated vitals (stamping TTFB from Navigation Timing) off the current page.</summary>
    public static async Task<WebVitalsSample> CollectAsync(IPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        var json = await page.EvaluateAsync<string>(
            "() => { const nav = performance.getEntriesByType('navigation')[0];" +
            " window.__vitals.ttfb = nav ? nav.responseStart : 0;" +
            " return JSON.stringify(window.__vitals); }").ConfigureAwait(false);

        return JsonSerializer.Deserialize<WebVitalsSample>(json) ?? new WebVitalsSample();
    }

    /// <summary>
    /// Writes the sample as a citable JSON artifact under <c>WEB_VITALS_OUTPUT_DIR</c> (set by the CI
    /// workflow to the uploaded <c>artifacts/</c> dir) or, when unset, <c>artifacts/</c> under the CWD.
    /// </summary>
    public static async Task WriteArtifactAsync(string label, string path, WebVitalsSample sample)
    {
        var dir = Environment.GetEnvironmentVariable("WEB_VITALS_OUTPUT_DIR")
            ?? Path.Combine(Directory.GetCurrentDirectory(), "artifacts");
        Directory.CreateDirectory(dir);

        var payload = new WebVitalsArtifact(label, path, sample);
        var file = Path.Combine(dir, $"web-vitals-{label}.json");
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(payload, WriteOptions)).ConfigureAwait(false);
    }
}

/// <summary>One page's measured Core Web Vitals (milliseconds, except unitless CLS).</summary>
public sealed record WebVitalsSample
{
    [JsonPropertyName("lcp")] public double Lcp { get; init; }

    [JsonPropertyName("cls")] public double Cls { get; init; }

    [JsonPropertyName("fcp")] public double Fcp { get; init; }

    [JsonPropertyName("ttfb")] public double Ttfb { get; init; }

    [JsonPropertyName("inp")] public double Inp { get; init; }
}

/// <summary>The artifact envelope written to <c>web-vitals-{label}.json</c>.</summary>
public sealed record WebVitalsArtifact(string Label, string Path, WebVitalsSample Vitals);

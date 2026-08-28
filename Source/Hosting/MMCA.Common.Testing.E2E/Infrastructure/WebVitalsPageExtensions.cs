using Microsoft.Playwright;

namespace MMCA.Common.Testing.E2E.Infrastructure;

/// <summary>
/// The one-call Core Web Vitals measurement every consumer's <c>WebVitalsTests</c> hand-rolled as a
/// private <c>MeasureAsync</c>: install the collector, load the page, optionally drive one scripted
/// interaction so an INP sample is recorded, collect, write the dated artifact, and assert the sample
/// against a budget. The order is the load-bearing part (the collector's observers must be installed
/// BEFORE the navigation, or LCP/FCP/TTFB are never recorded for that load), which is exactly what a
/// per-repo copy gets wrong once and then carries.
/// </summary>
public static class WebVitalsPageExtensions
{
    extension(IPage page)
    {
        /// <summary>
        /// Measures one page's Core Web Vitals and asserts them against <paramref name="budget"/>.
        /// </summary>
        /// <param name="label">The artifact label (also the failure-message label), e.g. <c>"home"</c>.</param>
        /// <param name="path">The path to load.</param>
        /// <param name="budget">The budget to assert against.</param>
        /// <param name="writeLine">Test output sink for the single-line sample record, or null.</param>
        /// <param name="interactionPlaceholder">
        /// The placeholder of an input to click and type into so the event-timing observer records a
        /// single INP latency sample. Null measures the load only. Best-effort: an absent or invisible
        /// field is skipped, and if no event clears the 16 ms threshold INP stays 0 and its assertion
        /// is skipped by the budget.
        /// </param>
        /// <param name="interactionText">The text typed into that input.</param>
        /// <returns>The collected sample, for any further app-specific assertion.</returns>
        public async Task<WebVitalsSample> MeasureWebVitalsAsync(
            string label,
            string path,
            WebVitalsBudget budget,
            Action<string>? writeLine = null,
            string? interactionPlaceholder = null,
            string interactionText = "test")
        {
            ArgumentNullException.ThrowIfNull(page);
            ArgumentNullException.ThrowIfNull(budget);

            await WebVitalsCollector.InstallAsync(page).ConfigureAwait(false);
            await page.GotoAndWaitForBlazorAsync(path).ConfigureAwait(false);

            if (interactionPlaceholder is not null)
            {
                var field = page.GetByPlaceholder(interactionPlaceholder);
                if (await field.IsVisibleAsync().ConfigureAwait(false))
                {
                    await field.ClickAsync().ConfigureAwait(false);
                    await field.FillAsync(interactionText).ConfigureAwait(false);
                }

                // Give the event-timing observer a slice to record the interaction it just saw.
                await page.WaitForTimeoutAsync(300).ConfigureAwait(false);
            }

            var sample = await WebVitalsCollector.CollectAsync(page).ConfigureAwait(false);
            await WebVitalsCollector.WriteArtifactAsync(label, path, sample).ConfigureAwait(false);

            budget.AssertWithinBudget(sample, label, path, writeLine);
            return sample;
        }
    }
}

using System.Globalization;
using AwesomeAssertions;
using Microsoft.Extensions.Localization;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Http;
using MMCA.Common.UI.Components.Forms;

namespace MMCA.Common.UI.Tests.Components.Forms;

/// <summary>
/// X-01 (local test run 7) at the component a page actually renders: <see cref="ErrorSummary"/>
/// uses <c>Localizer ?? SharedLocalizer</c>, so a page that passes its own localizer (the documented
/// ADR-027 usage) lost the framework's <c>Http.*</c> translations and showed the English synthesized
/// sentence for a bodiless 429 in a Spanish UI. Runs against the real <c>SharedResource</c>
/// localizer the base class registers (no stub override), with a page localizer that has no
/// <c>Http.*</c> keys.
/// </summary>
public sealed class ErrorSummaryPageLocalizerFallbackTests : BunitTestBase
{
    // SharedResource.es.resx "Http.429".
    private const string Spanish429 = "Demasiadas solicitudes. Espere un momento e int\u00E9ntelo de nuevo.";

    private const string RawStatusSentence = "The request failed with HTTP status code";

    [Fact]
    public void ABodiless429_WithAPageLocalizer_RendersTheSpanishSharedTranslation()
    {
        var previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("es-ES");
        try
        {
            var result = ProblemDetailsResultReader.ToFailureResult(429, null);

            var cut = RenderUnderTest<ErrorSummary>(p => p
                .Add(c => c.Result, result)
                .Add(c => c.Localizer, new PageLocalizer()));

            cut.Markup.Should().NotContain(
                RawStatusSentence,
                "X-01: a Spanish user must not read the English synthesized sentence because the page passed its own localizer");
            cut.Markup.Should().Contain(
                Spanish429,
                "X-01: ErrorSummary given a page Localizer must still render the SharedResource translation of Http.429");
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    [Fact]
    public void APageKey_WithAPageLocalizer_StillResolvesThroughThePageLocalizer()
    {
        // The fallback must not stop a page's own keys from resolving through the page localizer.
        var cut = RenderUnderTest<ErrorSummary>(p => p
            .Add(c => c.Result, Result.Failure(new Error("Test.Code", "Order.NotFound", ErrorType.NotFound)))
            .Add(c => c.Localizer, new PageLocalizer(("Order.NotFound", "That order no longer exists."))));

        cut.Markup.Should().Contain("That order no longer exists.");
    }

    /// <summary>
    /// A consumer page's localizer: it resolves its own page keys and reports
    /// <c>ResourceNotFound</c> for everything else, including every <c>Http.*</c> key.
    /// </summary>
    private sealed class PageLocalizer(params (string Key, string Value)[] entries) : IStringLocalizer
    {
        public LocalizedString this[string name]
        {
            get
            {
                foreach (var (key, value) in entries)
                {
                    if (string.Equals(key, name, StringComparison.Ordinal))
                    {
                        return new LocalizedString(name, value, resourceNotFound: false);
                    }
                }

                return new LocalizedString(name, name, resourceNotFound: true);
            }
        }

        public LocalizedString this[string name, params object[] arguments] => this[name];

        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) =>
            entries.Select(entry => new LocalizedString(entry.Key, entry.Value, resourceNotFound: false));
    }
}

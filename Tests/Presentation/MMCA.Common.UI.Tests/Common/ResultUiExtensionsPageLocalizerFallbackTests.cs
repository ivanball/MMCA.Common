using System.Globalization;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Http;
using MMCA.Common.UI.Common;
using MMCA.Common.UI.Common.Interfaces;
using Moq;

namespace MMCA.Common.UI.Tests.Common;

/// <summary>
/// X-01 (local test run 7): the client-synthesized HTTP failures (a bodiless 429 from the gateway
/// rate limiter, a bodiless 5xx, any status with no body) carry translations ONLY in the framework's
/// own <c>SharedResource</c> pair (<c>Http.{status}</c> and the generic <c>Http.Status</c> format).
/// A consumer page localizes through its OWN <c>IStringLocalizer&lt;PageType&gt;</c>, which has no
/// <c>Http.*</c> keys, so the lookup by code missed and the English synthesized sentence ("The
/// request failed with HTTP status code 429.") reached a Spanish user. The contract pinned here:
/// whatever localizer the page passes, a synthesized HTTP failure renders the SharedResource
/// translation for the current UI culture. The expected strings are copied from
/// <c>SharedResource.resx</c> / <c>SharedResource.es.resx</c>.
/// </summary>
public sealed class ResultUiExtensionsPageLocalizerFallbackTests
{
    // SharedResource.resx "Http.429".
    private const string English429 = "Too many requests. Wait a moment and try again.";

    // SharedResource.es.resx "Http.429".
    private const string Spanish429 = "Demasiadas solicitudes. Espere un momento e int\u00E9ntelo de nuevo.";

    // SharedResource.resx "Http.502".
    private const string English502 = "The service is temporarily unavailable. Please try again in a moment.";

    // SharedResource.es.resx "Http.502".
    private const string Spanish502 = "El servicio no est\u00E1 disponible temporalmente. Int\u00E9ntelo de nuevo en unos momentos.";

    // SharedResource.es.resx "Http.Status" formatted with 418 (a status with no Http.{status} key of its own).
    private const string SpanishGeneric418 = "La solicitud fall\u00F3 con el c\u00F3digo de estado HTTP 418.";

    public static TheoryData<string, int, string> SharedTranslations => new()
    {
        { "es-ES", 429, Spanish429 },
        { "en-US", 429, English429 },
        { "es-ES", 502, Spanish502 },
        { "en-US", 502, English502 },
        { "es-ES", 418, SpanishGeneric418 },
    };

    [Theory]
    [MemberData(nameof(SharedTranslations))]
    public void LocalizedErrorMessage_WithAPageLocalizerLackingHttpKeys_RendersTheSharedTranslation(
        string culture, int status, string expected)
    {
        var result = ProblemDetailsResultReader.ToFailureResult(status, null);

        var message = Under(culture, () => result.LocalizedErrorMessage(new PageLocalizer()));

        message.Should().Be(
            expected,
            $"X-01: a synthesized HTTP {status.ToString(CultureInfo.InvariantCulture)} must fall back to the SharedResource translation under {culture} when the page's own localizer has no Http.* keys");
    }

    [Theory]
    [MemberData(nameof(SharedTranslations))]
    public void LocalizedErrorMessages_WithAPageLocalizerLackingHttpKeys_RendersTheSharedTranslation(
        string culture, int status, string expected)
    {
        var result = ProblemDetailsResultReader.ToFailureResult(status, null);

        var messages = Under(culture, () => result.LocalizedErrorMessages(new PageLocalizer()));

        messages.Should().Equal(
            [expected],
            $"X-01: a synthesized HTTP {status.ToString(CultureInfo.InvariantCulture)} must fall back to the SharedResource translation under {culture} when the page's own localizer has no Http.* keys");
    }

    [Fact]
    public void LocalizedErrorMessage_WithARealPageTypeLocalizer_RendersTheSpanishSharedTranslation()
    {
        // A real ResourceManager-backed localizer for a type with no resx of its own: the shape a
        // consumer page's IStringLocalizer<PageType> has for every Http.* key.
        var result = ProblemDetailsResultReader.ToFailureResult(429, null);

        var message = Under("es-ES", () =>
        {
            using var provider = new ServiceCollection()
                .AddLogging()
                .AddLocalization()
                .BuildServiceProvider();
            var pageLocalizer = provider.GetRequiredService<IStringLocalizer<ResultUiExtensionsPageLocalizerFallbackTests>>();
            return result.LocalizedErrorMessage(pageLocalizer);
        });

        message.Should().Be(
            Spanish429,
            "X-01: a page's own IStringLocalizer<PageType> has no Http.429 key, so the SharedResource translation must be used");
    }

    [Fact]
    public void NotifyOnFailure_WithAPageLocalizerLackingHttpKeys_ToastsTheSpanishSharedTranslation()
    {
        var toast = new Mock<IToastService>();
        var result = ProblemDetailsResultReader.ToFailureResult(429, null);

        Under("es-ES", () => result.NotifyOnFailure(toast.Object, new PageLocalizer()));

        var shown = toast.Invocations
            .Where(invocation => invocation.Method.Name == nameof(IToastService.Show))
            .Select(invocation => invocation.Arguments[0] as string)
            .ToList();
        shown.Should().Equal(
            [Spanish429],
            "X-01: the toast for a synthesized 429 must carry the SharedResource Spanish translation, not the English synthesized sentence");
    }

    [Fact]
    public void AServerPhrasedMessage_IsStillShownVerbatim_ThroughAPageLocalizer()
    {
        // The fallback applies only to the client-synthesized failures: a message the server
        // already phrased must not be replaced by a shared translation.
        const string serverMessage = "El nombre es obligatorio.";
        var result = Result.Failure(Error.Validation("Validation.Name", serverMessage));

        Under("es-ES", () => result.LocalizedErrorMessage(new PageLocalizer())).Should().Be(serverMessage);
    }

    private static T Under<T>(string culture, Func<T> action)
    {
        var previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        try
        {
            return action();
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    /// <summary>
    /// A consumer page's localizer: its own resource pair holds page keys only, so every Http.* key
    /// comes back with <c>ResourceNotFound</c> set.
    /// </summary>
    private sealed class PageLocalizer : IStringLocalizer
    {
        public LocalizedString this[string name] => new(name, name, resourceNotFound: true);

        public LocalizedString this[string name, params object[] arguments] =>
            new(name, string.Format(CultureInfo.CurrentCulture, name, arguments), resourceNotFound: true);

        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => [];
    }
}

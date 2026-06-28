using System.Globalization;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.API;
using MMCA.Common.API.Localization;

namespace MMCA.Common.API.Tests.Localization;

/// <summary>
/// Verifies the edge error-localization seam (ADR-027): codes resolve to the current UI culture from the
/// framework's <c>ErrorResources</c> resx (including the Spanish satellite), and unknown/empty codes fall
/// back to the caller's English message.
/// </summary>
public sealed class ErrorLocalizerTests
{
    private static IErrorLocalizer CreateLocalizer()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddErrorLocalization();
        return services.BuildServiceProvider().GetRequiredService<IErrorLocalizer>();
    }

    private static void WithCulture(string culture, Action action)
    {
        var original = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo(culture);
            action();
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }
    }

    [Fact]
    public void Localize_KnownCode_UnderSpanish_ReturnsSpanish()
    {
        IErrorLocalizer localizer = CreateLocalizer();

        WithCulture("es", () =>
            localizer.Localize("PhoneNumber.Empty", "Phone number cannot be empty.")
                .Should().Be("El número de teléfono no puede estar vacío."));
    }

    [Fact]
    public void Localize_KnownCode_UnderEnglish_ReturnsEnglish()
    {
        IErrorLocalizer localizer = CreateLocalizer();

        WithCulture("en-US", () =>
            localizer.Localize("PhoneNumber.Empty", "ignored fallback")
                .Should().Be("Phone number cannot be empty."));
    }

    [Fact]
    public void Localize_UnknownCode_ReturnsFallbackMessage()
    {
        IErrorLocalizer localizer = CreateLocalizer();

        WithCulture("es", () =>
            localizer.Localize("Totally.Unknown.Code", "the original english message")
                .Should().Be("the original english message"));
    }

    [Fact]
    public void Localize_EmptyCode_ReturnsFallbackMessage()
    {
        IErrorLocalizer localizer = CreateLocalizer();

        localizer.Localize(string.Empty, "fallback").Should().Be("fallback");
    }
}

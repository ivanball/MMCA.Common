using System.Globalization;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using MMCA.Common.API.Localization;

namespace MMCA.Common.API.Tests.Localization;

/// <summary>
/// Verifies the edge error-localization extension point (ADR-027): codes resolve to the current UI culture from the
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

    // ── Precedence: the more specific (module) source wins, Common is the fallback ──
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Localize_CodeDefinedByAModuleAndByCommon_ReturnsTheModuleText(bool moduleRegisteredFirst)
    {
        IErrorLocalizer localizer = CreateLocalizerWithModule(
            moduleRegisteredFirst,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["Auth.InvalidCredentials"] = "User account is deactivated." });

        WithCulture("en-US", () =>
            localizer.Localize("Auth.InvalidCredentials", "ignored fallback")
                .Should().Be("User account is deactivated.", "a module's own translation must not be shadowed by the framework's"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Localize_CodeDefinedOnlyByCommon_ReturnsTheCommonText(bool moduleRegisteredFirst)
    {
        IErrorLocalizer localizer = CreateLocalizerWithModule(
            moduleRegisteredFirst,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["Module.Only"] = "Module text." });

        WithCulture("en-US", () =>
            localizer.Localize("PhoneNumber.Empty", "ignored fallback")
                .Should().Be("Phone number cannot be empty."));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Localize_CodeDefinedByNeitherSource_ReturnsTheFallbackMessage(bool moduleRegisteredFirst)
    {
        IErrorLocalizer localizer = CreateLocalizerWithModule(
            moduleRegisteredFirst,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["Module.Only"] = "Module text." });

        WithCulture("en-US", () =>
            localizer.Localize("Totally.Unknown.Code", "the original english message")
                .Should().Be("the original english message"));
    }

    private static IErrorLocalizer CreateLocalizerWithModule(bool moduleRegisteredFirst, Dictionary<string, string> moduleEntries)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var moduleSource = new ErrorResourceSource(new DictionaryStringLocalizer(moduleEntries));
        if (moduleRegisteredFirst)
        {
            services.AddSingleton(moduleSource);
            services.AddErrorLocalization();
        }
        else
        {
            services.AddErrorLocalization();
            services.AddSingleton(moduleSource);
        }

        return services.BuildServiceProvider().GetRequiredService<IErrorLocalizer>();
    }

    /// <summary>A module resource set in memory: found keys return their text, others report not found.</summary>
    private sealed class DictionaryStringLocalizer(Dictionary<string, string> entries) : IStringLocalizer
    {
        public LocalizedString this[string name] =>
            entries.TryGetValue(name, out var value)
                ? new LocalizedString(name, value)
                : new LocalizedString(name, name, resourceNotFound: true);

        public LocalizedString this[string name, params object[] arguments] => this[name];

        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) =>
            entries.Select(entry => new LocalizedString(entry.Key, entry.Value));
    }
}

using System.Globalization;
using AwesomeAssertions;
using Microsoft.Extensions.Localization;
using MMCA.Common.Shared.Globalization;
using MMCA.Common.UI.Globalization;

namespace MMCA.Common.UI.Tests.Globalization;

public class PseudoLocalizationTests
{
    [Fact]
    public void Transform_WrapsValueInBracketSentinel()
    {
        var result = PseudoLocalizer.Transform("Hello");

        result.Should().StartWith("[!!");
        result.Should().EndWith("!!]");
    }

    [Fact]
    public void Transform_ExpandsLength()
    {
        const string input = "Save changes";

        PseudoLocalizer.Transform(input).Length.Should().BeGreaterThan(input.Length);
    }

    [Fact]
    public void Transform_AccentsLetters()
    {
        // Every letter gains a combining acute (U+0301), so the output differs from the ASCII input.
        var result = PseudoLocalizer.Transform("abc");

        result.Should().Contain("́");
    }

    [Fact]
    public void Transform_PreservesNumericPlaceholders()
    {
        var result = PseudoLocalizer.Transform("Saved {0} of {1}");

        result.Should().Contain("{0}");
        result.Should().Contain("{1}");
    }

    [Fact]
    public void Transform_DoesNotAccentInsideNamedPlaceholders()
    {
        var result = PseudoLocalizer.Transform("Hi {name}");

        result.Should().Contain("{name}");
    }

    [Fact]
    public void Transform_EmptyInput_ReturnsEmpty() =>
        PseudoLocalizer.Transform(string.Empty).Should().BeEmpty();

    [Fact]
    public void Indexer_UnderDefaultCulture_DelegatesUnchanged()
    {
        var sut = new PseudoStringLocalizer(new FakeStringLocalizer(new Dictionary<string, string> { ["Greeting"] = "Hello" }));

        RunUnderCulture(SupportedCultures.Default, () =>
        {
            var value = sut["Greeting"];

            value.Value.Should().Be("Hello");
            value.ResourceNotFound.Should().BeFalse();
        });
    }

    [Fact]
    public void Indexer_UnderPseudoCulture_Transforms()
    {
        var sut = new PseudoStringLocalizer(new FakeStringLocalizer(new Dictionary<string, string> { ["Greeting"] = "Hello" }));

        RunUnderCulture(SupportedCultures.PseudoLocale, () =>
        {
            var value = sut["Greeting"];

            value.Value.Should().StartWith("[!!");
            value.Value.Should().NotBe("Hello");
        });
    }

    [Fact]
    public void IndexerWithArguments_UnderPseudoCulture_TransformsTemplateThenFormats()
    {
        var sut = new PseudoStringLocalizer(new FakeStringLocalizer(new Dictionary<string, string> { ["Count"] = "You have {0} items" }));

        RunUnderCulture(SupportedCultures.PseudoLocale, () =>
        {
            var value = sut["Count", 5];

            value.Value.Should().StartWith("[!!");
            value.Value.Should().Contain("5"); // the argument is substituted after the template is pseudo-localized
        });
    }

    [Fact]
    public void Factory_WrapsCreatedLocalizer_SoPseudoActivatesPerCulture()
    {
        var factory = new PseudoStringLocalizerFactory(
            new FakeStringLocalizerFactory(new Dictionary<string, string> { ["Greeting"] = "Hello" }));

        var localizer = factory.Create(typeof(PseudoLocalizationTests));

        RunUnderCulture(SupportedCultures.PseudoLocale, () => localizer["Greeting"].Value.Should().StartWith("[!!"));
        RunUnderCulture(SupportedCultures.Default, () => localizer["Greeting"].Value.Should().Be("Hello"));
    }

    private static void RunUnderCulture(string culture, Action assert)
    {
        var original = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            assert();
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }
    }

    private sealed class FakeStringLocalizer(IReadOnlyDictionary<string, string> values) : IStringLocalizer
    {
        public LocalizedString this[string name] =>
            values.TryGetValue(name, out var value)
                ? new LocalizedString(name, value, resourceNotFound: false)
                : new LocalizedString(name, name, resourceNotFound: true);

        public LocalizedString this[string name, params object[] arguments]
        {
            get
            {
                var template = this[name];
                return new LocalizedString(
                    name,
                    string.Format(CultureInfo.CurrentCulture, template.Value, arguments),
                    template.ResourceNotFound);
            }
        }

        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) =>
            values.Select(kv => new LocalizedString(kv.Key, kv.Value, resourceNotFound: false));
    }

    private sealed class FakeStringLocalizerFactory(IReadOnlyDictionary<string, string> values) : IStringLocalizerFactory
    {
        public IStringLocalizer Create(Type resourceSource) => new FakeStringLocalizer(values);

        public IStringLocalizer Create(string baseName, string location) => new FakeStringLocalizer(values);
    }
}

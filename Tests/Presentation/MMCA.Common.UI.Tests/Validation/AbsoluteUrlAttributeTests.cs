using System.ComponentModel.DataAnnotations;
using AwesomeAssertions;
using Microsoft.Extensions.Localization;
using MMCA.Common.UI.Validation;

namespace MMCA.Common.UI.Tests.Validation;

/// <summary>
/// Unit tests for <see cref="AbsoluteUrlAttribute"/>: the client-side mirror of the server's
/// <c>AbsoluteUrlRules</c>. What matters is that the two agree, including on the schemes they refuse
/// (these values are rendered into image sources and link targets), and that the message still
/// resolves as a localization resource key through the model validator.
/// </summary>
public sealed class AbsoluteUrlAttributeTests
{
    [Theory]
    [InlineData("https://example.com")]
    [InlineData("http://example.com")]
    [InlineData("https://example.com/logo.png?v=2")]
    [InlineData("HTTPS://EXAMPLE.COM")]
    public void Accepts_AbsoluteHttpAndHttpsUrls(string url) =>
        Validate(url).Should().BeEmpty();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    // Pairing with [Required] is how a mandatory field is expressed. If this rule also rejected
    // blanks, a blank required field would show two messages saying the same thing.
    public void Accepts_AbsentValues_BecauseOptionalityIsRequiredAttributesJob(string? url) =>
        Validate(url).Should().BeEmpty();

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html;base64,PHNjcmlwdD4=")]
    [InlineData("ftp://example.com/logo.png")]
    [InlineData("/images/logo.png")]
    [InlineData("example.com")]
    [InlineData("not a url")]
    public void Rejects_EverythingThatIsNotAnAbsoluteHttpUrl(string url) =>
        Validate(url).Should().ContainSingle();

    [Fact]
    public void ErrorMessage_IsEmittedUnchanged_SoItCanBeALocalizationResourceKey()
    {
        // DataAnnotationsModelValidator resolves whatever message it receives against the page's
        // localizer. That only works if the attribute hands the key over untouched.
        var model = new UrlModel { Website = "javascript:alert(1)" };
        var validator = new DataAnnotationsModelValidator(
            new StubLocalizer(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Validation.AbsoluteUrl"] = "Enter a full web address.",
            }));

        validator.Validate(model, nameof(UrlModel.Website)).Should().ContainSingle()
            .Which.Should().Be("Enter a full web address.");
    }

    [Fact]
    public void ErrorMessage_FallsThroughUnchanged_WhenItIsNotAKnownResourceKey()
    {
        var model = new UrlModel { Website = "javascript:alert(1)" };
        var validator = new DataAnnotationsModelValidator(
            new StubLocalizer(new Dictionary<string, string>(StringComparer.Ordinal)));

        validator.Validate(model, nameof(UrlModel.Website)).Should().ContainSingle()
            .Which.Should().Be("Validation.AbsoluteUrl");
    }

    [Fact]
    public void ReportsTheOffendingMember_SoTheMessageLandsOnTheRightField()
    {
        var model = new UrlModel { Website = "ftp://example.com" };
        var context = new ValidationContext(model) { MemberName = nameof(UrlModel.Website) };
        var results = new List<ValidationResult>();

        Validator.TryValidateProperty(model.Website, context, results);

        results.Should().ContainSingle()
            .Which.MemberNames.Should().ContainSingle().Which.Should().Be(nameof(UrlModel.Website));
    }

    private static List<ValidationResult> Validate(string? url)
    {
        var model = new BareUrlModel { Url = url };
        var context = new ValidationContext(model) { MemberName = nameof(BareUrlModel.Url) };
        var results = new List<ValidationResult>();

        Validator.TryValidateProperty(url, context, results);
        return results;
    }

    /// <summary>A model carrying the rule alone, with the attribute's own default message.</summary>
    private sealed class BareUrlModel
    {
        [AbsoluteUrl]
        public string? Url { get; init; }
    }

    /// <summary>A model whose message is a localization resource key, the shipped idiom.</summary>
    private sealed class UrlModel
    {
        [AbsoluteUrl(ErrorMessage = "Validation.AbsoluteUrl")]
        public string? Website { get; init; }
    }

    /// <summary>A localizer that knows only the keys it is handed.</summary>
    private sealed class StubLocalizer(Dictionary<string, string> entries) : IStringLocalizer
    {
        public LocalizedString this[string name] =>
            entries.TryGetValue(name, out string? value)
                ? new LocalizedString(name, value, resourceNotFound: false)
                : new LocalizedString(name, name, resourceNotFound: true);

        public LocalizedString this[string name, params object[] arguments] => this[name];

        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => [];
    }
}

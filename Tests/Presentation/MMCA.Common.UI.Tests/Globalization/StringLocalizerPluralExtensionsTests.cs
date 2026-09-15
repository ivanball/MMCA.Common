using System.Globalization;
using AwesomeAssertions;
using Microsoft.Extensions.Localization;
using MMCA.Common.UI.Globalization;

namespace MMCA.Common.UI.Tests.Globalization;

/// <summary>
/// Covers the plural-aware resource lookup (ADR-027): the one/other key selection, the format
/// arguments reaching the resolved key, and the base-key fallback that keeps a resource set which
/// has not been split yet from leaking a raw key name to the reader.
/// </summary>
public sealed class StringLocalizerPluralExtensionsTests
{
    /// <summary>
    /// Dictionary-backed localizer that reports a miss the way a ResourceManager-backed one does:
    /// the key name as the value and <c>ResourceNotFound</c> set.
    /// </summary>
    private sealed class FakeLocalizer(Dictionary<string, string> values) : IStringLocalizer
    {
        public LocalizedString this[string name] =>
            values.TryGetValue(name, out var value)
                ? new LocalizedString(name, value, resourceNotFound: false)
                : new LocalizedString(name, name, resourceNotFound: true);

        public LocalizedString this[string name, params object[] arguments] =>
            values.TryGetValue(name, out var value)
                ? new LocalizedString(name, string.Format(CultureInfo.CurrentCulture, value, arguments), resourceNotFound: false)
                : new LocalizedString(name, name, resourceNotFound: true);

        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) =>
            values.Select(kvp => new LocalizedString(kvp.Key, kvp.Value, resourceNotFound: false));
    }

    private static FakeLocalizer Split() => new(new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Notif.Send.SentTo"] = "Notification sent to {0} recipients.",
        ["Notif.Send.SentTo.One"] = "Notification sent to {0} recipient.",
        ["Notif.Send.SentTo.Other"] = "Notification sent to {0} recipients.",
    });

    [Fact]
    public void Plural_CountOfOne_ResolvesTheOneKey()
    {
        var result = Split().Plural("Notif.Send.SentTo", 1, 1);

        result.Name.Should().Be("Notif.Send.SentTo.One");
        result.Value.Should().Be("Notification sent to 1 recipient.");
        result.ResourceNotFound.Should().BeFalse();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(10)]
    // Zero takes the other form in both cultures the framework ships, which is why there is no
    // separate zero suffix.
    public void Plural_AnyOtherCount_ResolvesTheOtherKey(int count)
    {
        var result = Split().Plural("Notif.Send.SentTo", count, count);

        result.Name.Should().Be("Notif.Send.SentTo.Other");
        result.Value.Should().Be(string.Create(CultureInfo.InvariantCulture, $"Notification sent to {count} recipients."));
    }

    [Fact]
    // A resource set that only declares the base key keeps working: the reader sees the single
    // message rather than the string "Widgets.Count.One".
    public void Plural_WhenThePluralKeyIsMissing_FallsBackToTheBaseKey()
    {
        var localizer = new FakeLocalizer(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Notif.Send.SentTo"] = "Notification sent to {0} recipients.",
        });

        var result = localizer.Plural("Notif.Send.SentTo", 1, 1);

        result.Name.Should().Be("Notif.Send.SentTo");
        result.Value.Should().Be("Notification sent to 1 recipients.");
        result.ResourceNotFound.Should().BeFalse();
    }

    [Fact]
    // Nothing declared at all: the miss is reported rather than silently swallowed, so the caller
    // (and the resource-coverage gate) can still see it.
    public void Plural_WhenNothingIsDeclared_ReportsTheMissOnTheBaseKey()
    {
        var localizer = new FakeLocalizer(new Dictionary<string, string>(StringComparer.Ordinal));

        var result = localizer.Plural("Widgets.Count", 3, 3);

        result.Name.Should().Be("Widgets.Count");
        result.ResourceNotFound.Should().BeTrue();
    }

    [Fact]
    public void Plural_RejectsAnEmptyKey()
    {
        var localizer = Split();

        Assert.Throws<ArgumentException>(() => localizer.Plural(string.Empty, 1));
    }
}

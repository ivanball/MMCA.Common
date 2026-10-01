using System.Text.RegularExpressions;
using MMCA.Common.Testing.Architecture;

namespace MMCA.Common.Architecture.Tests.Ui;

/// <summary>
/// Fails on a resource value that renders a count inside a fixed plural sentence ("{0} recipients",
/// "{0} item(s)"), which reads "1 recipients" or "1 item(s)" for a count of one. A count-sensitive
/// message declares <c>.One</c> / <c>.Other</c> siblings and is resolved through
/// <c>StringLocalizerPluralExtensions.Plural</c> (ADR-027), so a placeholder followed by a plural noun
/// is accepted only on a <c>.Other</c> key or on the base key of such a pair (its fallback). Scoped to
/// the framework's own resources; <c>MudTranslations</c> is excluded because MudBlazor owns and
/// formats those keys and cannot select a plural form.
/// </summary>
public sealed partial class PluralSentenceResourceTests
{
    [Fact]
    public void FrameworkResources_HaveNoCountInAFixedPluralSentence()
    {
        var sourceRoot = Path.Combine(ArchitectureMapBase.FindRepoRoot("MMCA.Common.slnx"), "Source");
        var files = Directory
            .EnumerateFiles(sourceRoot, "*.resx", SearchOption.AllDirectories)
            .Where(p => !Path.GetFileName(p).StartsWith("MudTranslations", StringComparison.Ordinal)
                && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();

        // Non-vacuous floor: SharedResource (base + es) and ErrorResources (base + es).
        files.Should().HaveCountGreaterThanOrEqualTo(4, "a wrong scan root must not let the check pass having read nothing");

        var violations = files.SelectMany(file =>
            FixedPluralSentences(ReadEntries(file)).Select(v => $"  - {Path.GetFileName(file)}: {v}"));

        ArchitectureAssert.NoViolations(
            violations,
            "a count rendered inside a fixed plural sentence reads wrongly for a count of one. Split the key into "
            + "KEY.One and KEY.Other siblings and resolve it with L.Plural(KEY, count, count) (ADR-027)");
    }

    [Theory]
    [InlineData("Cart.Items", "{0} items in your cart")]
    [InlineData("Cart.Items", "{0} item(s) in your cart")]
    [InlineData("Notif.Sent", "Enviada a {0} destinatarios.")]
    [InlineData("Reviews.Count", "({0} reviews)")]
    public void Detector_Flags_ACountInAFixedPluralSentence(string key, string value) =>
        FixedPluralSentences([new(key, value)]).Should().ContainSingle(
            "a single-form key renders the plural noun for a count of one");

    [Theory]
    [InlineData("Notif.Sent.Other", "Sent to {0} recipients.")]
    [InlineData("Common.Error.NotFound", "{0} with Id {1} was not found.")]
    [InlineData("Auth.Register.EmailAlreadyRegistered", "An account for {0} is already registered.")]
    [InlineData("Auth.Sessions.Device.Format", "{0} on {1}")]
    [InlineData("Notif.Send.SentTo.One", "Sent to {0} recipient.")]
    public void Detector_Accepts_AValueThatIsNotAFixedPluralSentence(string key, string value) =>
        FixedPluralSentences([new(key, value)]).Should().BeEmpty(
            "a .Other key, a singular noun or a verb after the placeholder is not the defect");

    [Fact]
    public void Detector_Accepts_TheBaseKeyOfAPluralPair_AsItsFallback() =>
        FixedPluralSentences(
        [
            new("Notif.Sent", "Sent to {0} recipients."),
            new("Notif.Sent.One", "Sent to {0} recipient."),
            new("Notif.Sent.Other", "Sent to {0} recipients."),
        ]).Should().BeEmpty("the base key is only the fallback for a resource set not yet split");

    /// <summary>One resource entry: its key and its value.</summary>
    private sealed record ResourceEntry(string Key, string Value);

    private static List<ResourceEntry> ReadEntries(string resxPath) =>
        [.. XDocument.Load(resxPath).Root!
            .Elements("data")
            .Select(d => new ResourceEntry(
                (string?)d.Attribute("name") ?? string.Empty,
                (string?)d.Element("value") ?? string.Empty))];

    /// <summary>
    /// Returns one line per entry rendering a count inside a fixed plural sentence: a placeholder
    /// followed by a word marked plural, either "(s)" / "(es)" or a word of four or more letters
    /// ending in "s" that is not a known non-noun. A <c>.Other</c> key, and the base key of a pair
    /// that declares a <c>.Other</c> sibling, are the sanctioned plural forms and are skipped.
    /// </summary>
    private static List<string> FixedPluralSentences(IReadOnlyList<ResourceEntry> entries)
    {
        var keys = entries.Select(e => e.Key).ToHashSet(StringComparer.Ordinal);
        return [.. entries
            .Where(e => !e.Key.EndsWith(".Other", StringComparison.Ordinal)
                && !keys.Contains(e.Key + ".Other"))
            .SelectMany(e => CountFollowedByPlural.Matches(e.Value)
                .Select(m => m.Groups["word"].Value)
                .Where(IsPluralMarked)
                .Select(word => $"{e.Key} = \"{e.Value}\" (count followed by '{word}')"))];
    }

    private static bool IsPluralMarked(string word) =>
        word.EndsWith("(s)", StringComparison.OrdinalIgnoreCase)
        || word.EndsWith("(es)", StringComparison.OrdinalIgnoreCase)
        || word.Length >= 4
            && word.EndsWith('s')
            && !NonNounsEndingInS.Contains(word);

    /// <summary>English and Spanish words ending in "s" that follow a count without being its noun.</summary>
    private static readonly HashSet<string> NonNounsEndingInS = new(StringComparer.OrdinalIgnoreCase)
    {
        "does", "this", "always", "perhaps", "unless", "across", "pues", "tras", "antes", "mientras", "menos", "entonces", "ademas", "despues",
    };

    /// <summary>A format placeholder ({0}, {1:N0}), whitespace, then the word that follows it.</summary>
    [GeneratedRegex(@"\{\d+(?:[:,][^}]*)?\}\s+(?<word>\p{L}+(?:\((?:e?s)\))?)", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex CountFollowedByPlural { get; }
}

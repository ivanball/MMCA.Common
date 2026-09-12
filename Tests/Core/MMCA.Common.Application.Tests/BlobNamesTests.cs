using AwesomeAssertions;
using MMCA.Common.Application.Interfaces.Infrastructure.Storage;

namespace MMCA.Common.Application.Tests;

/// <summary>
/// Tests for <see cref="BlobNames"/> (ADR-045): a user-supplied file name becomes part of a URL, so the
/// sanitizer is the boundary that keeps path traversal, unicode and unbounded length out of a blob name
/// while still leaving something a human recognizes.
/// </summary>
public sealed class BlobNamesTests
{
    [Theory]
    [InlineData("deck.pptx", "deck.pptx")]
    [InlineData("Quarterly Report.PDF", "Quarterly-Report.pdf")]
    [InlineData("budget_2026-final.xlsx", "budget_2026-final.xlsx")]
    [InlineData("notes...txt", "notes.txt")]
    [InlineData("  spaced out .md", "spaced-out.md")]
    public void SanitizeFileName_WithOrdinaryNames_KeepsTheRecognizableShape(string fileName, string expected) =>
        BlobNames.SanitizeFileName(fileName).Should().Be(expected);

    [Fact]
    public void SanitizeFileName_LowerCasesTheExtensionButNotTheStem() =>
        BlobNames.SanitizeFileName("AnnualReport.PDF").Should().Be("AnnualReport.pdf");

    [Fact]
    public void SanitizeFileName_WithUnicode_ReplacesRunsWithASingleDash() =>
        BlobNames.SanitizeFileName("plan-f\u00FCr-2026.pdf").Should().Be("plan-f-r-2026.pdf");

    [Fact]
    public void SanitizeFileName_WithNonLatinScript_CollapsesToTheFallbackStem() =>
        BlobNames.SanitizeFileName("\u6587\u4EF6.pdf").Should().Be("file.pdf");

    [Fact]
    public void SanitizeFileName_WithRunsOfUnsafeCharacters_EmitsOneDashPerRun() =>
        BlobNames.SanitizeFileName("a   b!!!c***d.txt").Should().Be("a-b-c-d.txt");

    [Theory]
    [InlineData("../../x.pdf")]
    [InlineData("..\\..\\x.pdf")]
    [InlineData("/etc/passwd.txt")]
    [InlineData("C:\\Windows\\System32\\drivers\\etc\\hosts.txt")]
    [InlineData("a..b/../c.pdf")]
    public void SanitizeFileName_WithPathTraversal_LeavesNoSeparatorAndNoDoubleDot(string fileName)
    {
        string sanitized = BlobNames.SanitizeFileName(fileName);

        sanitized.Should().NotContain("..").And.NotContain("/").And.NotContain("\\");
        sanitized.Should().MatchRegex("^[A-Za-z0-9._-]+$");
    }

    [Fact]
    public void SanitizeFileName_WithPathTraversal_KeepsOnlyTheLeafAndItsExtension() =>
        BlobNames.SanitizeFileName("../../x.pdf").Should().Be("x.pdf");

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("...")]
    [InlineData("---")]
    [InlineData(".-.-.")]
    [InlineData("!!!")]
    [InlineData("\u0001\u0002")]
    public void SanitizeFileName_WithNothingWorthKeeping_ReturnsTheFallback(string fileName) =>
        BlobNames.SanitizeFileName(fileName).Should().Be("file");

    [Theory]
    [InlineData("archive", "archive")]
    [InlineData("READ ME", "READ-ME")]
    public void SanitizeFileName_WithNoExtension_ReturnsTheStemAlone(string fileName, string expected) =>
        BlobNames.SanitizeFileName(fileName).Should().Be(expected);

    [Fact]
    public void SanitizeFileName_WithALeadingDotName_TreatsItAsAStemNotAnExtension() =>
        BlobNames.SanitizeFileName(".gitignore").Should().Be("gitignore");

    [Fact]
    public void SanitizeFileName_WithAVeryLongName_CapsAtTheDefaultLengthAndKeepsTheExtension()
    {
        string sanitized = BlobNames.SanitizeFileName(new string('a', 500) + ".pptx");

        sanitized.Should().HaveLength(100);
        sanitized.Should().EndWith(".pptx");
        sanitized.Should().StartWith(new string('a', 95));
    }

    [Fact]
    public void SanitizeFileName_WithAnExplicitMaxLength_HonorsIt()
    {
        string sanitized = BlobNames.SanitizeFileName("a-very-long-presentation-name.pptx", maxLength: 20);

        sanitized.Should().HaveLength(20);
        sanitized.Should().EndWith(".pptx");
    }

    [Fact]
    public void SanitizeFileName_WhenTruncationLandsOnASeparator_TrimsIt() =>
        BlobNames.SanitizeFileName("abcdef ghijkl.txt", maxLength: 11).Should().Be("abcdef.txt");

    [Fact]
    public void SanitizeFileName_WithAnExtensionLongerThanTheBudget_StillPreservesTheExtension()
    {
        string sanitized = BlobNames.SanitizeFileName("report.averyveryverylongextension", maxLength: 5);

        sanitized.Should().Be("r.averyveryverylongextension");
    }

    [Fact]
    public void SanitizeFileName_WithANullName_Throws()
    {
        Action act = () => BlobNames.SanitizeFileName(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void SanitizeFileName_WithANonPositiveMaxLength_Throws(int maxLength)
    {
        Action act = () => BlobNames.SanitizeFileName("deck.pptx", maxLength);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}

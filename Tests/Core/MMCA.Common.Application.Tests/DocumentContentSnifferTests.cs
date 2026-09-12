using System.Globalization;
using System.IO.Compression;
using System.Text;
using AwesomeAssertions;
using MMCA.Common.Application.Interfaces.Infrastructure.Storage;

namespace MMCA.Common.Application.Tests;

/// <summary>
/// Table tests for <see cref="DocumentContentSniffer"/> (ADR-045): an upload is accepted only when the
/// BYTES and the file-name extension agree on a format the caller allowed. Fixtures are built in memory
/// (a PDF header, real and fake Office Open XML packages, UTF-8 text, a NUL-bearing payload, invalid
/// UTF-8, html), so every mapping row, every mismatch, the allowed mask and the zip-bomb bail-out are
/// covered without touching disk.
/// </summary>
public sealed class DocumentContentSnifferTests
{
    private const string PresentationType = "application/vnd.openxmlformats-officedocument.presentationml.presentation";
    private const string WordType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
    private const string SpreadsheetType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private static byte[] Pdf() => [.. "%PDF-1.4\n1 0 obj\n<< /Type /Catalog >>\nendobj\n"u8];

    private static byte[] Text(string value) => Encoding.UTF8.GetBytes(value);

    /// <summary>A zip carrying the Office Open XML package root, i.e. what a real .pptx/.docx/.xlsx is.</summary>
    private static byte[] OfficeOpenXmlPackage() => Zip("[Content_Types].xml", "word/document.xml", "_rels/.rels");

    /// <summary>An ordinary zip: the same signature, no package root.</summary>
    private static byte[] PlainZip() => Zip("readme.txt", "data/values.csv");

    private static byte[] Zip(params string[] entryNames)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (string name in entryNames)
            {
                ZipArchiveEntry entry = archive.CreateEntry(name);
                using var writer = new StreamWriter(entry.Open());
                writer.Write("x");
            }
        }

        return buffer.ToArray();
    }

    // ── IsPdf ──
    [Fact]
    public void IsPdf_WithPdfHeader_ReturnsTrue() =>
        DocumentContentSniffer.IsPdf(Pdf()).Should().BeTrue();

    [Fact]
    public void IsPdf_WithTruncatedHeader_ReturnsFalse() =>
        DocumentContentSniffer.IsPdf("%PDF"u8).Should().BeFalse();

    [Fact]
    public void IsPdf_WithZipContent_ReturnsFalse() =>
        DocumentContentSniffer.IsPdf(PlainZip()).Should().BeFalse();

    [Fact]
    public void IsPdf_WithEmptyContent_ReturnsFalse() =>
        DocumentContentSniffer.IsPdf([]).Should().BeFalse();

    // ── IsZip ──
    [Fact]
    public void IsZip_WithLocalFileHeaderSignature_ReturnsTrue() =>
        DocumentContentSniffer.IsZip(PlainZip()).Should().BeTrue();

    [Fact]
    public void IsZip_WithOfficeOpenXmlPackage_ReturnsTrue() =>
        DocumentContentSniffer.IsZip(OfficeOpenXmlPackage()).Should().BeTrue();

    [Fact]
    public void IsZip_WithPdfContent_ReturnsFalse() =>
        DocumentContentSniffer.IsZip(Pdf()).Should().BeFalse();

    [Fact]
    public void IsZip_WithTruncatedSignature_ReturnsFalse() =>
        DocumentContentSniffer.IsZip([0x50, 0x4B, 0x03]).Should().BeFalse();

    // ── IsOfficeOpenXml ──
    [Fact]
    public void IsOfficeOpenXml_WithContentTypesEntry_ReturnsTrue() =>
        DocumentContentSniffer.IsOfficeOpenXml(OfficeOpenXmlPackage()).Should().BeTrue();

    [Fact]
    public void IsOfficeOpenXml_WithPlainZip_ReturnsFalse() =>
        DocumentContentSniffer.IsOfficeOpenXml(PlainZip()).Should().BeFalse();

    [Fact]
    public void IsOfficeOpenXml_WithNonZipContent_ReturnsFalse() =>
        DocumentContentSniffer.IsOfficeOpenXml(Pdf()).Should().BeFalse();

    [Fact]
    public void IsOfficeOpenXml_WithTruncatedArchive_ReturnsFalseRatherThanThrowing()
    {
        byte[] package = OfficeOpenXmlPackage();

        DocumentContentSniffer.IsOfficeOpenXml(package.AsMemory(0, package.Length / 2)).Should().BeFalse();
    }

    [Fact]
    public void IsOfficeOpenXml_WithZipSignatureButGarbageBody_ReturnsFalseRatherThanThrowing() =>
        DocumentContentSniffer.IsOfficeOpenXml(new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x11, 0x22, 0x33, 0x44 })
            .Should().BeFalse();

    [Fact]
    public void IsOfficeOpenXml_WithMoreThanFourThousandEntries_BailsOutAndReturnsFalse()
    {
        string[] names = new string[5000];
        names[0] = "[Content_Types].xml";
        for (int index = 1; index < names.Length; index++)
        {
            names[index] = "part-" + index.ToString(CultureInfo.InvariantCulture) + ".xml";
        }

        DocumentContentSniffer.IsOfficeOpenXml(Zip(names)).Should().BeFalse(
            "an archive declaring more than 4096 entries is refused before any entry is inspected");
    }

    // ── IsPlainUtf8Text ──
    [Fact]
    public void IsPlainUtf8Text_WithAsciiText_ReturnsTrue() =>
        DocumentContentSniffer.IsPlainUtf8Text(Text("hello world")).Should().BeTrue();

    [Fact]
    public void IsPlainUtf8Text_WithMultiByteText_ReturnsTrue() =>
        DocumentContentSniffer.IsPlainUtf8Text(Text("greetings: \u00FC \u00E4 \u00F6 \u20AC \U0001F600")).Should().BeTrue();

    [Fact]
    public void IsPlainUtf8Text_WithByteOrderMark_ReturnsTrue() =>
        DocumentContentSniffer.IsPlainUtf8Text([0xEF, 0xBB, 0xBF, .. Text("# Title")]).Should().BeTrue();

    [Fact]
    public void IsPlainUtf8Text_WithBomOnly_ReturnsFalse() =>
        DocumentContentSniffer.IsPlainUtf8Text([0xEF, 0xBB, 0xBF]).Should().BeFalse();

    [Fact]
    public void IsPlainUtf8Text_WithEmbeddedNul_ReturnsFalse() =>
        DocumentContentSniffer.IsPlainUtf8Text([.. Text("MZ"), 0x00, .. Text("payload")]).Should().BeFalse();

    [Fact]
    public void IsPlainUtf8Text_WithInvalidUtf8_ReturnsFalse() =>
        DocumentContentSniffer.IsPlainUtf8Text([0xC3, 0x28, 0xA0, 0xA1]).Should().BeFalse();

    [Fact]
    public void IsPlainUtf8Text_WithEmptyContent_ReturnsFalse() =>
        DocumentContentSniffer.IsPlainUtf8Text([]).Should().BeFalse();

    // ── Detect: every mapping row ──
    [Fact]
    public void Detect_WithPdfBytesAndPdfName_ReturnsPdfType() =>
        DocumentContentSniffer.Detect(Pdf(), "report.pdf", DocumentFormats.All).Should().Be("application/pdf");

    [Fact]
    public void Detect_WithPackageBytesAndPptxName_ReturnsPresentationType() =>
        DocumentContentSniffer.Detect(OfficeOpenXmlPackage(), "deck.pptx", DocumentFormats.All).Should().Be(PresentationType);

    [Fact]
    public void Detect_WithPackageBytesAndDocxName_ReturnsWordType() =>
        DocumentContentSniffer.Detect(OfficeOpenXmlPackage(), "contract.docx", DocumentFormats.All).Should().Be(WordType);

    [Fact]
    public void Detect_WithPackageBytesAndXlsxName_ReturnsSpreadsheetType() =>
        DocumentContentSniffer.Detect(OfficeOpenXmlPackage(), "budget.xlsx", DocumentFormats.All).Should().Be(SpreadsheetType);

    [Fact]
    public void Detect_WithZipBytesAndZipName_ReturnsZipType() =>
        DocumentContentSniffer.Detect(PlainZip(), "bundle.zip", DocumentFormats.All).Should().Be("application/zip");

    [Fact]
    public void Detect_WithTextBytesAndTxtName_ReturnsPlainTextType() =>
        DocumentContentSniffer.Detect(Text("notes"), "notes.txt", DocumentFormats.All).Should().Be("text/plain");

    [Theory]
    [InlineData("readme.md")]
    [InlineData("readme.markdown")]
    public void Detect_WithTextBytesAndMarkdownName_ReturnsMarkdownType(string fileName) =>
        DocumentContentSniffer.Detect(Text("# Heading"), fileName, DocumentFormats.All).Should().Be("text/markdown");

    [Fact]
    public void Detect_WithUpperCaseExtension_ReturnsTheSameType() =>
        DocumentContentSniffer.Detect(Pdf(), "REPORT.PDF", DocumentFormats.All).Should().Be("application/pdf");

    [Fact]
    public void Detect_OverReadOnlyMemory_AgreesWithTheSpanOverload() =>
        DocumentContentSniffer.Detect(OfficeOpenXmlPackage().AsMemory(), "deck.pptx", DocumentFormats.All)
            .Should().Be(PresentationType);

    // ── Detect: byte/extension mismatches ──
    [Fact]
    public void Detect_WithZipBytesUnderAPdfName_ReturnsNull() =>
        DocumentContentSniffer.Detect(PlainZip(), "invoice.pdf", DocumentFormats.All).Should().BeNull();

    [Fact]
    public void Detect_WithPdfBytesUnderADocxName_ReturnsNull() =>
        DocumentContentSniffer.Detect(Pdf(), "contract.docx", DocumentFormats.All).Should().BeNull();

    [Fact]
    public void Detect_WithPlainZipUnderAPptxName_ReturnsNull() =>
        DocumentContentSniffer.Detect(PlainZip(), "deck.pptx", DocumentFormats.All).Should().BeNull(
            "a zip without [Content_Types].xml is not an Office Open XML package");

    [Fact]
    public void Detect_WithPackageBytesUnderAZipName_ReturnsZipType() =>
        DocumentContentSniffer.Detect(OfficeOpenXmlPackage(), "bundle.zip", DocumentFormats.All)
            .Should().Be("application/zip", "a package IS a zip, and the caller asked for a zip");

    [Fact]
    public void Detect_WithBinaryBytesUnderATxtName_ReturnsNull() =>
        DocumentContentSniffer.Detect([0x4D, 0x5A, 0x00, 0x90], "setup.txt", DocumentFormats.All).Should().BeNull();

    [Theory]
    [InlineData("page.html")]
    [InlineData("logo.svg")]
    [InlineData("setup.exe")]
    [InlineData("slides.ppt")]
    [InlineData("letter.doc")]
    [InlineData("photo.jpg")]
    public void Detect_WithAnUnsupportedExtension_ReturnsNull(string fileName) =>
        DocumentContentSniffer.Detect(Text("<html><body>hello</body></html>"), fileName, DocumentFormats.All)
            .Should().BeNull();

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("noextension")]
    [InlineData(".pdf")]
    [InlineData("trailingdot.")]
    public void Detect_WithABlankOrExtensionlessName_ReturnsNull(string fileName) =>
        DocumentContentSniffer.Detect(Pdf(), fileName, DocumentFormats.All).Should().BeNull();

    [Fact]
    public void Detect_WithEmptyContent_ReturnsNull() =>
        DocumentContentSniffer.Detect([], "report.pdf", DocumentFormats.All).Should().BeNull();

    // ── Detect: the allowed mask ──
    [Fact]
    public void Detect_WithPdfNotAllowed_ReturnsNull() =>
        DocumentContentSniffer.Detect(Pdf(), "report.pdf", DocumentFormats.OfficeOpenXml).Should().BeNull();

    [Fact]
    public void Detect_WithOnlyPdfAllowed_StillAcceptsPdf() =>
        DocumentContentSniffer.Detect(Pdf(), "report.pdf", DocumentFormats.Pdf).Should().Be("application/pdf");

    [Fact]
    public void Detect_WithOnlyPresentationAllowed_RejectsAWordDocument() =>
        DocumentContentSniffer.Detect(OfficeOpenXmlPackage(), "contract.docx", DocumentFormats.Presentation)
            .Should().BeNull();

    [Fact]
    public void Detect_WithOfficeOpenXmlAllowed_AcceptsAllThreePayloads()
    {
        byte[] package = OfficeOpenXmlPackage();

        DocumentContentSniffer.Detect(package, "deck.pptx", DocumentFormats.OfficeOpenXml).Should().Be(PresentationType);
        DocumentContentSniffer.Detect(package, "contract.docx", DocumentFormats.OfficeOpenXml).Should().Be(WordType);
        DocumentContentSniffer.Detect(package, "budget.xlsx", DocumentFormats.OfficeOpenXml).Should().Be(SpreadsheetType);
    }

    [Fact]
    public void Detect_WithOfficeOpenXmlAllowed_DoesNotAcceptAPlainZip() =>
        DocumentContentSniffer.Detect(PlainZip(), "bundle.zip", DocumentFormats.OfficeOpenXml).Should().BeNull();

    [Fact]
    public void Detect_WithPlainTextAllowedButNotMarkdown_RejectsMarkdown() =>
        DocumentContentSniffer.Detect(Text("# Heading"), "readme.md", DocumentFormats.PlainText).Should().BeNull();

    [Fact]
    public void Detect_WithNoFormatsAllowed_ReturnsNullForEveryFixture()
    {
        DocumentContentSniffer.Detect(Pdf(), "report.pdf", DocumentFormats.None).Should().BeNull();
        DocumentContentSniffer.Detect(OfficeOpenXmlPackage(), "deck.pptx", DocumentFormats.None).Should().BeNull();
        DocumentContentSniffer.Detect(PlainZip(), "bundle.zip", DocumentFormats.None).Should().BeNull();
        DocumentContentSniffer.Detect(Text("notes"), "notes.txt", DocumentFormats.None).Should().BeNull();
    }

    [Fact]
    public void DocumentFormats_All_CoversEveryIndividualFormat() =>
        DocumentFormats.All.Should().Be(
            DocumentFormats.Pdf | DocumentFormats.Presentation | DocumentFormats.WordDocument
            | DocumentFormats.Spreadsheet | DocumentFormats.Zip | DocumentFormats.PlainText | DocumentFormats.Markdown);
}

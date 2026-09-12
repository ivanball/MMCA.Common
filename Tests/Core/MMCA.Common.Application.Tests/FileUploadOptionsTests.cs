using AwesomeAssertions;
using MMCA.Common.Application.Interfaces.Infrastructure.Storage;

namespace MMCA.Common.Application.Tests;

/// <summary>
/// Tests for <see cref="FileUploadOptions"/> (ADR-045): the disposition builders produce an RFC 6266
/// header with an ASCII fallback AND an RFC 5987 <c>filename*</c>, and a hostile file name cannot break
/// out of the header (quotes, semicolons and line breaks are dropped rather than escaped).
/// </summary>
public sealed class FileUploadOptionsTests
{
    [Fact]
    public void None_SetsNeitherHeader()
    {
        FileUploadOptions.None.ContentDisposition.Should().BeNull();
        FileUploadOptions.None.CacheControl.Should().BeNull();
    }

    [Fact]
    public void Attachment_WithAnAsciiName_BuildsBothFilenameForms() =>
        FileUploadOptions.Attachment("deck.pptx").ContentDisposition
            .Should().Be("attachment; filename=\"deck.pptx\"; filename*=UTF-8''deck.pptx");

    [Fact]
    public void Inline_WithAnAsciiName_UsesTheInlineDispositionType() =>
        FileUploadOptions.Inline("report.pdf").ContentDisposition
            .Should().Be("inline; filename=\"report.pdf\"; filename*=UTF-8''report.pdf");

    [Fact]
    public void Attachment_WithANonAsciiName_FallsBackToAsciiAndPercentEncodesUtf8() =>
        FileUploadOptions.Attachment("plan-f\u00FCr-2026.pdf").ContentDisposition
            .Should().Be("attachment; filename=\"plan-f_r-2026.pdf\"; filename*=UTF-8''plan-f%C3%BCr-2026.pdf");

    [Fact]
    public void Inline_WithANonAsciiName_EncodesTheSameWayAsAttachment() =>
        FileUploadOptions.Inline("plan-f\u00FCr-2026.pdf").ContentDisposition
            .Should().Be("inline; filename=\"plan-f_r-2026.pdf\"; filename*=UTF-8''plan-f%C3%BCr-2026.pdf");

    [Fact]
    public void Attachment_WithAQuoteAndASemicolon_DropsThemFromTheAsciiFallback()
    {
        string? disposition = FileUploadOptions.Attachment("qu\"ote; drop.pdf").ContentDisposition;

        disposition.Should().Be("attachment; filename=\"quote drop.pdf\"; filename*=UTF-8''qu%22ote%3B%20drop.pdf");
        disposition.Should().NotBeNull();
        disposition![..disposition.IndexOf("filename*", StringComparison.Ordinal)]
            .Should().Be("attachment; filename=\"quote drop.pdf\"; ", "the quoted-string must close exactly once");
    }

    [Fact]
    public void Attachment_WithLineBreaks_DropsThemSoTheHeaderCannotBeSplit()
    {
        string? disposition = FileUploadOptions.Attachment("evil\r\nX-Injected: 1.pdf").ContentDisposition;

        disposition.Should().NotContain("\r").And.NotContain("\n");
        disposition.Should().StartWith("attachment; filename=\"evilX-Injected: 1.pdf\"");
    }

    [Fact]
    public void Attachment_WithABackslash_DropsItFromTheAsciiFallback() =>
        FileUploadOptions.Attachment("c:\\temp\\deck.pptx").ContentDisposition
            .Should().StartWith("attachment; filename=\"c:tempdeck.pptx\"");

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Attachment_WithABlankName_UsesTheGenericFallback(string fileName) =>
        FileUploadOptions.Attachment(fileName).ContentDisposition
            .Should().Be("attachment; filename=\"file\"; filename*=UTF-8''file");

    [Fact]
    public void Attachment_WithACacheControl_CarriesItThrough() =>
        FileUploadOptions.Attachment("deck.pptx", "public, max-age=31536000, immutable").CacheControl
            .Should().Be("public, max-age=31536000, immutable");

    [Fact]
    public void Attachment_WithoutACacheControl_LeavesTheHeaderUnset() =>
        FileUploadOptions.Attachment("deck.pptx").CacheControl.Should().BeNull();

    [Fact]
    public void Attachment_WithANullName_Throws()
    {
        Action act = () => FileUploadOptions.Attachment(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Inline_WithANullName_Throws()
    {
        Action act = () => FileUploadOptions.Inline(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void WithExpression_KeepsTheRecordSemantics()
    {
        FileUploadOptions options = FileUploadOptions.Attachment("deck.pptx") with { CacheControl = "no-store" };

        options.CacheControl.Should().Be("no-store");
        options.ContentDisposition.Should().Be("attachment; filename=\"deck.pptx\"; filename*=UTF-8''deck.pptx");
    }
}

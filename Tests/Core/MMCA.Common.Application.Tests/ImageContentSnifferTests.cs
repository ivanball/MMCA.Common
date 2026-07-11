using AwesomeAssertions;
using MMCA.Common.Application.Interfaces.Infrastructure;

namespace MMCA.Common.Application.Tests;

/// <summary>
/// Table tests for <see cref="ImageContentSniffer"/> (ADR-045): the accepted upload formats are
/// decided by leading magic bytes only. Positives cover jpeg/png/webp at both the exact minimum
/// signature length and with trailing payload; negatives cover empty, truncated, corrupted, and
/// wrong-format prefixes (gif/bmp/pdf/text and a RIFF container that is not WebP).
/// </summary>
public sealed class ImageContentSnifferTests
{
    private static readonly byte[] JpegSignature = [0xFF, 0xD8, 0xFF];
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static byte[] WebPHeader(string formType = "WEBP") =>
    [
        (byte)'R', (byte)'I', (byte)'F', (byte)'F',
        0x24, 0x00, 0x00, 0x00, // arbitrary RIFF chunk size
        (byte)formType[0], (byte)formType[1], (byte)formType[2], (byte)formType[3],
    ];

    // ── JPEG ──
    [Fact]
    public void IsJpeg_WithExactSoiMarkerPrefix_ReturnsTrue() =>
        ImageContentSniffer.IsJpeg(JpegSignature).Should().BeTrue();

    [Fact]
    public void IsJpeg_WithJfifPayloadAfterTheMarker_ReturnsTrue() =>
        ImageContentSniffer.IsJpeg([0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46]).Should().BeTrue();

    [Fact]
    public void IsJpeg_WithTruncatedTwoByteMarker_ReturnsFalse() =>
        ImageContentSniffer.IsJpeg([0xFF, 0xD8]).Should().BeFalse();

    [Fact]
    public void IsJpeg_WithCorruptedThirdByte_ReturnsFalse() =>
        ImageContentSniffer.IsJpeg([0xFF, 0xD8, 0x00, 0xE0]).Should().BeFalse();

    // ── PNG ──
    [Fact]
    public void IsPng_WithExactEightByteSignature_ReturnsTrue() =>
        ImageContentSniffer.IsPng(PngSignature).Should().BeTrue();

    [Fact]
    public void IsPng_WithIhdrPayloadAfterTheSignature_ReturnsTrue() =>
        ImageContentSniffer.IsPng([.. PngSignature, 0x00, 0x00, 0x00, 0x0D, (byte)'I', (byte)'H', (byte)'D', (byte)'R']).Should().BeTrue();

    [Fact]
    public void IsPng_WithTruncatedSevenByteSignature_ReturnsFalse() =>
        ImageContentSniffer.IsPng(PngSignature.AsSpan(0, 7)).Should().BeFalse();

    [Fact]
    public void IsPng_WithOneFlippedSignatureByte_ReturnsFalse()
    {
        byte[] corrupted = [.. PngSignature];
        corrupted[5] = 0x0B;

        ImageContentSniffer.IsPng(corrupted).Should().BeFalse();
    }

    // ── WebP ──
    [Fact]
    public void IsWebP_WithExactTwelveByteHeader_ReturnsTrue() =>
        ImageContentSniffer.IsWebP(WebPHeader()).Should().BeTrue();

    [Fact]
    public void IsWebP_WithVp8PayloadAfterTheHeader_ReturnsTrue() =>
        ImageContentSniffer.IsWebP([.. WebPHeader(), (byte)'V', (byte)'P', (byte)'8', (byte)' ']).Should().BeTrue();

    [Fact]
    public void IsWebP_WithTruncatedElevenByteHeader_ReturnsFalse() =>
        ImageContentSniffer.IsWebP(WebPHeader().AsSpan(0, 11)).Should().BeFalse();

    [Fact]
    public void IsWebP_WithRiffContainerButWaveFormType_ReturnsFalse() =>
        ImageContentSniffer.IsWebP(WebPHeader(formType: "WAVE")).Should().BeFalse();

    [Fact]
    public void IsWebP_WithWebPFormTypeButNoRiffPrefix_ReturnsFalse()
    {
        byte[] content = WebPHeader();
        content[0] = (byte)'X';

        ImageContentSniffer.IsWebP(content).Should().BeFalse();
    }

    // ── IsAllowedImage: accepted formats ──
    [Fact]
    public void IsAllowedImage_WithJpegContent_ReturnsTrue() =>
        ImageContentSniffer.IsAllowedImage([0xFF, 0xD8, 0xFF, 0xE1]).Should().BeTrue();

    [Fact]
    public void IsAllowedImage_WithPngContent_ReturnsTrue() =>
        ImageContentSniffer.IsAllowedImage(PngSignature).Should().BeTrue();

    [Fact]
    public void IsAllowedImage_WithWebPContent_ReturnsTrue() =>
        ImageContentSniffer.IsAllowedImage(WebPHeader()).Should().BeTrue();

    // ── IsAllowedImage: rejected content ──
    [Theory]
    [MemberData(nameof(RejectedContent))]
    public void IsAllowedImage_WithNonImageOrTruncatedContent_ReturnsFalse(string label, byte[] content) =>
        ImageContentSniffer.IsAllowedImage(content).Should().BeFalse(
            $"'{label}' does not start with an accepted image signature");

    [Fact]
    public void IsAllowedImage_WithEmptyContent_ReturnsFalse() =>
        ImageContentSniffer.IsAllowedImage([]).Should().BeFalse();

    public static TheoryData<string, byte[]> RejectedContent() => new()
    {
        { "single byte", [0xFF] },
        { "truncated jpeg", [0xFF, 0xD8] },
        { "truncated png", [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A] },
        { "gif87a", [(byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'7', (byte)'a', 0x00, 0x00, 0x00, 0x00, 0x00, 0x00] },
        { "bmp", [(byte)'B', (byte)'M', 0x36, 0x00, 0x0C, 0x00, 0x00, 0x00, 0x00, 0x00, 0x36, 0x00] },
        { "pdf", [(byte)'%', (byte)'P', (byte)'D', (byte)'F', (byte)'-', (byte)'1', (byte)'.', (byte)'7', 0x0A, 0x25, 0x25, 0x25] },
        { "riff wave", [(byte)'R', (byte)'I', (byte)'F', (byte)'F', 0x24, 0x00, 0x00, 0x00, (byte)'W', (byte)'A', (byte)'V', (byte)'E'] },
        { "plain text", "hello world!"u8.ToArray() },
        { "declared-content-type spoof (html)", "<html><body>"u8.ToArray() },
    };
}

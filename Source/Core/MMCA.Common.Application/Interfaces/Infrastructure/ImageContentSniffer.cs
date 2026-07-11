namespace MMCA.Common.Application.Interfaces.Infrastructure;

/// <summary>
/// Dependency-free magic-byte sniffing for uploaded images: the accepted formats are decided by the
/// actual bytes, never the client-declared content type or file extension. The upload-side companion
/// to <see cref="IImageProcessor"/> (ADR-045): callers narrow the accepted inputs to jpeg/png/webp
/// here, then hand the content to the processor, whose re-encoding keeps only pixels. App-specific
/// size limits and error codes stay in the calling handler.
/// </summary>
public static class ImageContentSniffer
{
    /// <summary>Whether the content starts like a JPEG, PNG, or WebP image.</summary>
    /// <param name="content">The uploaded bytes, from the start of the payload.</param>
    /// <returns><see langword="true"/> when the leading bytes match one of the accepted formats.</returns>
    public static bool IsAllowedImage(ReadOnlySpan<byte> content) =>
        IsJpeg(content) || IsPng(content) || IsWebP(content);

    /// <summary>Whether the content starts with the JPEG SOI marker prefix (<c>FF D8 FF</c>).</summary>
    /// <param name="content">The uploaded bytes, from the start of the payload.</param>
    /// <returns><see langword="true"/> when the leading bytes match the JPEG signature.</returns>
    public static bool IsJpeg(ReadOnlySpan<byte> content) =>
        content.Length >= 3 && content[0] == 0xFF && content[1] == 0xD8 && content[2] == 0xFF;

    /// <summary>Whether the content starts with the 8-byte PNG signature.</summary>
    /// <param name="content">The uploaded bytes, from the start of the payload.</param>
    /// <returns><see langword="true"/> when the leading bytes match the PNG signature.</returns>
    public static bool IsPng(ReadOnlySpan<byte> content) =>
        content.Length >= 8 && content[..8].SequenceEqual((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

    /// <summary>Whether the content starts with a RIFF container declaring the <c>WEBP</c> form type.</summary>
    /// <param name="content">The uploaded bytes, from the start of the payload.</param>
    /// <returns><see langword="true"/> when the leading bytes match the WebP signature.</returns>
    public static bool IsWebP(ReadOnlySpan<byte> content) =>
        content.Length >= 12
        && content[..4].SequenceEqual("RIFF"u8)
        && content[8..12].SequenceEqual("WEBP"u8);
}

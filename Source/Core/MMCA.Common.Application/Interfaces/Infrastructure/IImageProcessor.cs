using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Application.Interfaces.Infrastructure;

/// <summary>
/// Normalizes untrusted uploaded images (ADR-045): decodes (rejecting non-images), corrects
/// EXIF orientation, center-crops to a square of the requested size, strips ALL metadata (EXIF
/// GPS coordinates are PII), and re-encodes as JPEG. Re-encoding is also the defense against
/// polyglot/malformed image payloads: only pixels survive.
/// </summary>
public interface IImageProcessor
{
    /// <summary>Decodes, square-crops to <paramref name="size"/> pixels, strips metadata, re-encodes as JPEG.</summary>
    /// <param name="content">The uploaded image bytes, read from the current position.</param>
    /// <param name="size">The output square edge length in pixels.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The normalized JPEG bytes, or a validation failure for undecodable content.</returns>
    Task<Result<byte[]>> NormalizeToSquareJpegAsync(Stream content, int size, CancellationToken cancellationToken = default);
}

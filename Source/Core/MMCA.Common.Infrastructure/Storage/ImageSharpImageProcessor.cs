using System.Globalization;
using MMCA.Common.Application.Interfaces.Infrastructure.Storage;
using MMCA.Common.Shared.Abstractions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace MMCA.Common.Infrastructure.Storage;

/// <summary>
/// ImageSharp implementation of <see cref="IImageProcessor"/> (ADR-045). Decoding + full
/// re-encode is deliberate: only pixels survive, so EXIF metadata (including GPS coordinates,
/// which are PII) and any polyglot payload embedded in the original file are discarded.
/// </summary>
public sealed class ImageSharpImageProcessor : IImageProcessor
{
    /// <summary>
    /// Ceiling on the DECODED pixel count (width x height) of an accepted upload: 50 megapixels,
    /// comfortably above any real camera image and far below what a decompression bomb declares.
    /// </summary>
    /// <remarks>
    /// SEC-Common-28. ADR-045's 2 MB cap bounds the COMPRESSED file, which a bomb satisfies: a 2 MB
    /// PNG can declare 40000x40000 and cost roughly 6 GB of frame buffer the moment it is decoded.
    /// A few concurrent uploads then take the replica out on an allocation the caller chose.
    /// </remarks>
    public const long MaxDecodedPixels = 50_000_000L;

    /// <summary>
    /// Ceiling on either decoded dimension. A 1 x 200000 strip is inside
    /// <see cref="MaxDecodedPixels"/> yet still pathological for the resampler, so the edges are
    /// bounded as well as the area.
    /// </summary>
    public const int MaxDecodedDimension = 20_000;

    /// <inheritdoc />
    public async Task<Result<byte[]>> NormalizeToSquareJpegAsync(Stream content, int size, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        try
        {
            // Header only: Identify reads the format's dimensions without allocating a single frame,
            // so a declared-oversize image is refused before the allocation it was built to provoke.
            // Reading the header first also keeps the failure a 400 (Error.Validation) rather than
            // the OutOfMemoryException that escaped the catch clause below as a 500.
            var startPosition = content.CanSeek ? content.Position : 0L;
            var info = await Image.IdentifyAsync(content, cancellationToken).ConfigureAwait(false);

            if (TooLargeToDecode(info.Width, info.Height))
            {
                return Result.Failure<byte[]>(Error.Validation(
                    code: "Image.TooLarge",
                    message: string.Create(CultureInfo.InvariantCulture, $"The uploaded image is too large to process ({info.Width}x{info.Height} pixels)."),
                    source: nameof(ImageSharpImageProcessor)));
            }

            if (content.CanSeek)
            {
                content.Position = startPosition;
            }

            using var image = await Image.LoadAsync(content, cancellationToken).ConfigureAwait(false);

            // Second gate, on the DECODED frame. A format whose header understates its real size (or
            // a multi-frame file) would otherwise slip past the header check.
            if (TooLargeToDecode(image.Width, image.Height))
            {
                return Result.Failure<byte[]>(Error.Validation(
                    code: "Image.TooLarge",
                    message: string.Create(CultureInfo.InvariantCulture, $"The uploaded image is too large to process ({image.Width}x{image.Height} pixels)."),
                    source: nameof(ImageSharpImageProcessor)));
            }

            // Bake the EXIF orientation into the pixels BEFORE stripping metadata, or portrait
            // phone photos come out rotated.
            image.Mutate(ctx => ctx
                .AutoOrient()
                .Resize(new ResizeOptions
                {
                    Size = new Size(size, size),
                    Mode = ResizeMode.Crop,
                }));

            image.Metadata.ExifProfile = null;
            image.Metadata.XmpProfile = null;
            image.Metadata.IptcProfile = null;

            var output = new MemoryStream();
            await using (output.ConfigureAwait(false))
            {
                await image.SaveAsync(output, new JpegEncoder { Quality = 85 }, cancellationToken).ConfigureAwait(false);
                return Result.Success(output.ToArray());
            }
        }
        catch (Exception ex) when (ex is UnknownImageFormatException or InvalidImageContentException)
        {
            return Result.Failure<byte[]>(Error.Validation(
                code: "Image.Undecodable",
                message: "The uploaded file is not a supported image.",
                source: nameof(ImageSharpImageProcessor)));
        }
    }

    /// <summary>
    /// Whether a frame of these dimensions is refused: either edge past
    /// <see cref="MaxDecodedDimension"/>, or the area past <see cref="MaxDecodedPixels"/>.
    /// </summary>
    /// <param name="width">Declared or decoded width in pixels.</param>
    /// <param name="height">Declared or decoded height in pixels.</param>
    /// <returns><see langword="true"/> when the frame must not be decoded or resampled.</returns>
    private static bool TooLargeToDecode(int width, int height) =>
        width > MaxDecodedDimension
        || height > MaxDecodedDimension
        || (long)width * height > MaxDecodedPixels;
}

using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Shared.Abstractions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace MMCA.Common.Infrastructure.Services;

/// <summary>
/// ImageSharp implementation of <see cref="IImageProcessor"/> (ADR-045). Decoding + full
/// re-encode is deliberate: only pixels survive, so EXIF metadata (including GPS coordinates,
/// which are PII) and any polyglot payload embedded in the original file are discarded.
/// </summary>
public sealed class ImageSharpImageProcessor : IImageProcessor
{
    /// <inheritdoc />
    public async Task<Result<byte[]>> NormalizeToSquareJpegAsync(Stream content, int size, CancellationToken cancellationToken = default)
    {
        try
        {
            using var image = await Image.LoadAsync(content, cancellationToken).ConfigureAwait(false);

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
}

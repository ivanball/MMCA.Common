using System.Buffers;
using System.Globalization;
using MMCA.Common.Application.Interfaces.Infrastructure.Storage;
using MMCA.Common.Shared.Abstractions;
using SkiaSharp;

namespace MMCA.Common.Infrastructure.Storage;

/// <summary>
/// SkiaSharp implementation of <see cref="IImageProcessor"/> (ADR-045). Decoding + full re-encode is
/// deliberate: the original bytes never reach the output, because the JPEG is encoded from a fresh
/// pixel buffer that carries no metadata at all, so EXIF (including GPS coordinates, which are PII),
/// XMP, IPTC, the ICC profile and any polyglot payload embedded in the original file are discarded.
/// </summary>
public sealed class SkiaSharpImageProcessor : IImageProcessor
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
    /// <remarks>SEC-Common-28, alongside <see cref="MaxDecodedPixels"/>.</remarks>
    public const int MaxDecodedDimension = 20_000;

    /// <summary>
    /// Ceiling on the ENCODED bytes read from the stream: 32 MiB. Callers already cap uploads far
    /// below this (ADR-045's 2 MB), so the bound only matters when a caller forgets to: the content is
    /// buffered in memory before decoding, and an unbounded read would let the stream length choose
    /// the allocation instead.
    /// </summary>
    private const int MaxEncodedBytes = 32 * 1024 * 1024;

    /// <summary>JPEG quality of the re-encoded output, matching the ImageSharp adapter this replaced.</summary>
    private const int JpegQuality = 85;

    /// <summary>Size of each chunk read from the upload stream.</summary>
    private const int ReadChunkBytes = 81_920;

    /// <summary>
    /// Linear filtering with linear mipmap blending: a large downscale (a 4000x3000 photo to a
    /// 256-pixel avatar) samples a pre-averaged mip level rather than skipping most source pixels, so
    /// the result does not alias, and an upscale of a small image is smoothly interpolated.
    /// </summary>
    private static readonly SKSamplingOptions Sampling = new(SKFilterMode.Linear, SKMipmapMode.Linear);

    /// <inheritdoc />
    public async Task<Result<byte[]>> NormalizeToSquareJpegAsync(Stream content, int size, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size);
        cancellationToken.ThrowIfCancellationRequested();

        // Buffer the upload: request-body streams cannot seek, and SKCodec needs to read the header
        // and then the pixels from the same bytes. The read is bounded, so the stream cannot pick the
        // size of this buffer.
        using var data = await ReadBoundedAsync(content, cancellationToken).ConfigureAwait(false);
        if (data is null)
        {
            return TooLarge(string.Create(CultureInfo.InvariantCulture, $"The uploaded image is too large to process (more than {MaxEncodedBytes} bytes)."));
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var codec = OpenAllowedCodec(data);
        if (codec is null)
        {
            return Undecodable();
        }

        // Header only: SKCodec.Create parses the format's header and allocates no frame, so a
        // declared-oversize image is refused before the allocation it was built to provoke. The
        // decode below writes into a buffer of exactly these dimensions, so no format can understate
        // its header and decode into more memory than was checked here.
        var width = codec.Info.Width;
        var height = codec.Info.Height;
        if (TooLargeToDecode(width, height))
        {
            return TooLarge(string.Create(CultureInfo.InvariantCulture, $"The uploaded image is too large to process ({width}x{height} pixels)."));
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var decoded = Decode(codec, width, height);
        if (decoded is null)
        {
            return Undecodable();
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var square = RenderCoverSquare(decoded, codec.EncodedOrigin, size);
        using var pixmap = square.PeekPixels();

        // Encoded from the fresh pixel buffer alone: it carries no color space and no metadata, so
        // nothing from the original file can be written back out.
        using var jpeg = pixmap.Encode(new SKJpegEncoderOptions(JpegQuality, SKJpegEncoderDownsample.Downsample420, SKJpegEncoderAlphaOption.Ignore));
        return jpeg is null ? Undecodable() : Result.Success(jpeg.ToArray());
    }

    /// <summary>
    /// Reads the rest of <paramref name="content"/> into memory, stopping as soon as it passes
    /// <see cref="MaxEncodedBytes"/>.
    /// </summary>
    /// <param name="content">The upload stream, read from its current position.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The buffered bytes, or <see langword="null"/> when the stream is over the ceiling.</returns>
    private static async Task<SKData?> ReadBoundedAsync(Stream content, CancellationToken cancellationToken)
    {
        var buffered = new MemoryStream();
        await using (buffered.ConfigureAwait(false))
        {
            var chunk = ArrayPool<byte>.Shared.Rent(ReadChunkBytes);
            try
            {
                int read;
                while ((read = await content.ReadAsync(chunk.AsMemory(0, ReadChunkBytes), cancellationToken).ConfigureAwait(false)) > 0)
                {
                    if (buffered.Length + read > MaxEncodedBytes)
                    {
                        return null;
                    }

                    await buffered.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(chunk);
            }

            return SKData.CreateCopy(new ReadOnlySpan<byte>(buffered.GetBuffer(), 0, (int)buffered.Length));
        }
    }

    /// <summary>
    /// Opens a codec over the buffered upload only when it is one of the allowed formats, checked
    /// twice. First by magic bytes, BEFORE Skia sees the content: Skia also decodes BMP, ICO, WBMP,
    /// HEIF, AVIF and RAW, and SKCodec.Create dispatches on the same leading bytes, so sniffing first
    /// means no decoder outside JPEG, PNG, WebP and GIF ever parses an upload. Then on the decoder
    /// Skia actually picked, which must agree with the sniff.
    /// </summary>
    /// <param name="data">The buffered upload.</param>
    /// <returns>
    /// The codec, or <see langword="null"/> when the content is not an allowed format or Skia cannot
    /// parse its header.
    /// </returns>
    private static SKCodec? OpenAllowedCodec(SKData data)
    {
        if (!HasAllowedSignature(data.AsSpan()))
        {
            return null;
        }

        // The codec takes ownership of the native stream; disposing the wrapper afterwards is safe.
        using var stream = new SKMemoryStream(data);
        SKCodec? codec = null;
        try
        {
            codec = SKCodec.Create(stream, out var result);
            if (result != SKCodecResult.Success || !IsAllowedFormat(codec.EncodedFormat))
            {
                return null;
            }

            var accepted = codec;
            codec = null;
            return accepted;
        }
        finally
        {
            codec?.Dispose();
        }
    }

    /// <summary>
    /// Decodes the FIRST frame only into a premultiplied bitmap of the header dimensions. Decoding
    /// every frame of an animated GIF or WebP is how a small upload becomes hundreds of megabytes of
    /// pixel buffers; the output is a single JPEG, so frame 0 is all it needs.
    /// </summary>
    /// <param name="codec">The codec over the buffered upload.</param>
    /// <param name="width">The checked header width.</param>
    /// <param name="height">The checked header height.</param>
    /// <returns>
    /// The decoded bitmap, or <see langword="null"/> when the decode is anything short of complete.
    /// A truncated scan reports <see cref="SKCodecResult.IncompleteInput"/> and leaves the rest of the
    /// frame blank; a partially decoded image is not a successful upload.
    /// </returns>
    private static SKBitmap? Decode(SKCodec codec, int width, int height)
    {
        var info = new SKImageInfo(width, height, SKImageInfo.PlatformColorType, SKAlphaType.Premul);
        var bitmap = new SKBitmap();
        if (!bitmap.TryAllocPixels(info)
            || codec.GetPixels(info, bitmap.GetPixels(), info.RowBytes, new SKCodecOptions(0)) != SKCodecResult.Success)
        {
            bitmap.Dispose();
            return null;
        }

        // Immutable, so drawing it wraps the pixels in an image without copying them first.
        bitmap.SetImmutable();
        return bitmap;
    }

    /// <summary>
    /// Draws <paramref name="source"/> onto a <paramref name="size"/>-pixel square: oriented as its
    /// EXIF origin says it displays, scaled so the SHORTER displayed edge fills the square (cover,
    /// upscaling small images), and centered so the overflow of the longer edge is cropped equally
    /// from both sides. Never letterboxed, never stretched.
    /// </summary>
    /// <param name="source">The decoded frame in its stored layout.</param>
    /// <param name="origin">The EXIF origin the codec read from the header.</param>
    /// <param name="size">The output square edge length in pixels.</param>
    /// <returns>An opaque square bitmap ready to encode.</returns>
    /// <remarks>
    /// The orientation is applied BEFORE the crop, as part of one transform: the crop has to be
    /// centered on the image as displayed, or a portrait phone photo (stored landscape, tagged
    /// "rotate 90") is cropped along the wrong axis and comes out rotated. The canvas transform maps
    /// output pixels back to source pixels in one resampling pass.
    /// </remarks>
    private static SKBitmap RenderCoverSquare(SKBitmap source, SKEncodedOrigin origin, int size)
    {
        var orientation = OrientationMatrix(origin);
        var swapsAxes = orientation.ScaleX == 0f;
        var displayedWidth = swapsAxes ? source.Height : source.Width;
        var displayedHeight = swapsAxes ? source.Width : source.Height;
        var scale = Math.Max((float)size / displayedWidth, (float)size / displayedHeight);

        var square = new SKBitmap(new SKImageInfo(size, size, SKImageInfo.PlatformColorType, SKAlphaType.Premul));
        using var canvas = new SKCanvas(square);

        // Transparent regions of a PNG, GIF or WebP flatten onto white, since JPEG has no alpha.
        canvas.Clear(SKColors.White);

        // Read bottom-up: center the stored frame on the origin, orient it, scale it to cover, then
        // move the displayed center to the center of the square.
        var half = size / 2f;
        canvas.Translate(half, half);
        canvas.Scale(scale);
        canvas.Concat(in orientation);
        canvas.Translate(-source.Width / 2f, -source.Height / 2f);

        using var paint = new SKPaint();
        paint.IsAntialias = true;
        canvas.DrawBitmap(source, 0f, 0f, Sampling, paint);
        canvas.Flush();
        return square;
    }

    /// <summary>
    /// The transform from a frame centered on the origin in its STORED layout to the same frame in
    /// its DISPLAYED layout, per the EXIF orientation (y grows downward). The four transposing
    /// origins swap the displayed width and height.
    /// </summary>
    /// <param name="origin">The EXIF origin.</param>
    /// <returns>A rotation and/or mirror with no scale and no translation.</returns>
    private static SKMatrix OrientationMatrix(SKEncodedOrigin origin) => origin switch
    {
        // Mirror horizontally.
        SKEncodedOrigin.TopRight => new SKMatrix(-1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f),

        // Rotate 180 degrees.
        SKEncodedOrigin.BottomRight => new SKMatrix(-1f, 0f, 0f, 0f, -1f, 0f, 0f, 0f, 1f),

        // Mirror vertically.
        SKEncodedOrigin.BottomLeft => new SKMatrix(1f, 0f, 0f, 0f, -1f, 0f, 0f, 0f, 1f),

        // Transpose: (x, y) -> (y, x).
        SKEncodedOrigin.LeftTop => new SKMatrix(0f, 1f, 0f, 1f, 0f, 0f, 0f, 0f, 1f),

        // Rotate 90 degrees clockwise: (x, y) -> (-y, x).
        SKEncodedOrigin.RightTop => new SKMatrix(0f, -1f, 0f, 1f, 0f, 0f, 0f, 0f, 1f),

        // Transverse: (x, y) -> (-y, -x).
        SKEncodedOrigin.RightBottom => new SKMatrix(0f, -1f, 0f, -1f, 0f, 0f, 0f, 0f, 1f),

        // Rotate 90 degrees counter-clockwise: (x, y) -> (y, -x).
        SKEncodedOrigin.LeftBottom => new SKMatrix(0f, 1f, 0f, -1f, 0f, 0f, 0f, 0f, 1f),

        // TopLeft (also named Default): the stored layout is the displayed one.
        SKEncodedOrigin.TopLeft => SKMatrix.Identity,

        // A value outside the EXIF range: treat it as untagged rather than guess a rotation.
        _ => SKMatrix.Identity,
    };

    /// <summary>
    /// Whether the content starts like one of the formats an upload may legitimately be: JPEG, PNG and
    /// WebP (what <see cref="ImageContentSniffer"/> admits) plus GIF.
    /// </summary>
    /// <param name="content">The buffered upload.</param>
    /// <returns><see langword="true"/> when the leading bytes match an allowed format.</returns>
    private static bool HasAllowedSignature(ReadOnlySpan<byte> content) =>
        ImageContentSniffer.IsAllowedImage(content) || IsGif(content);

    /// <summary>Whether the content starts with a GIF87a or GIF89a signature.</summary>
    /// <param name="content">The buffered upload.</param>
    /// <returns><see langword="true"/> when the leading bytes match the GIF signature.</returns>
    private static bool IsGif(ReadOnlySpan<byte> content) =>
        content.Length >= 6 && (content[..6].SequenceEqual("GIF87a"u8) || content[..6].SequenceEqual("GIF89a"u8));

    /// <summary>Whether the decoder Skia selected is one of the allowed formats.</summary>
    /// <param name="format">The codec's encoded format.</param>
    /// <returns><see langword="true"/> for JPEG, PNG, WebP and GIF.</returns>
    private static bool IsAllowedFormat(SKEncodedImageFormat format) =>
        format is SKEncodedImageFormat.Jpeg or SKEncodedImageFormat.Png or SKEncodedImageFormat.Webp or SKEncodedImageFormat.Gif;

    /// <summary>
    /// Whether a frame of these dimensions is refused: either edge past
    /// <see cref="MaxDecodedDimension"/>, or the area past <see cref="MaxDecodedPixels"/>.
    /// </summary>
    /// <param name="width">Declared width in pixels.</param>
    /// <param name="height">Declared height in pixels.</param>
    /// <returns><see langword="true"/> when the frame must not be decoded or resampled.</returns>
    private static bool TooLargeToDecode(int width, int height) =>
        width <= 0
        || height <= 0
        || width > MaxDecodedDimension
        || height > MaxDecodedDimension
        || (long)width * height > MaxDecodedPixels;

    private static Result<byte[]> TooLarge(string message) =>
        Result.Failure<byte[]>(Error.Validation(
            code: "Image.TooLarge",
            message: message,
            source: nameof(SkiaSharpImageProcessor)));

    private static Result<byte[]> Undecodable() =>
        Result.Failure<byte[]>(Error.Validation(
            code: "Image.Undecodable",
            message: "The uploaded file is not a supported image.",
            source: nameof(SkiaSharpImageProcessor)));
}

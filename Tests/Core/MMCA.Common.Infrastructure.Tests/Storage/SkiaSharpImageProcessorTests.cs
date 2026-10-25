using AwesomeAssertions;
using MMCA.Common.Infrastructure.Storage;
using MMCA.Common.Shared.Abstractions;
using SkiaSharp;

namespace MMCA.Common.Infrastructure.Tests.Storage;

/// <summary>
/// Tests for <see cref="SkiaSharpImageProcessor"/> (ADR-045): untrusted uploads come out as
/// exact-size square JPEGs, oriented, center-cropped to cover, with ALL metadata stripped (EXIF GPS
/// is PII). Only JPEG, PNG, WebP and GIF are accepted; anything else, and anything whose header
/// declares an oversized frame, fails as a validation error rather than an exception.
/// </summary>
public sealed class SkiaSharpImageProcessorTests
{
    private const int Size = 256;

    private readonly SkiaSharpImageProcessor _sut = new();

    // R1
    [Fact]
    public async Task NormalizeToSquareJpeg_WithALandscapePng_ProducesAnExactSquareJpeg()
    {
        var png = ImageFixtures.CreateSolid(SKEncodedImageFormat.Png, 640, 480, ImageFixtures.Solid);

        var result = await NormalizeAsync(png);

        AssertSquareJpeg(result);
    }

    // R2
    [Fact]
    public async Task NormalizeToSquareJpeg_WithASmallPng_UpscalesToTheRequestedSize()
    {
        var png = ImageFixtures.CreateSolid(SKEncodedImageFormat.Png, 64, 48, ImageFixtures.Solid);

        var result = await NormalizeAsync(png);

        AssertSquareJpeg(result);
    }

    // R3
    [Fact]
    public async Task NormalizeToSquareJpeg_WithACameraSizedPng_Succeeds()
    {
        var png = ImageFixtures.CreateSolid(SKEncodedImageFormat.Png, 4000, 3000, ImageFixtures.Solid);

        var result = await NormalizeAsync(png);

        AssertSquareJpeg(result);
    }

    // R4
    [Fact]
    public async Task NormalizeToSquareJpeg_WithNonImageContent_FailsAsUndecodable()
    {
        var result = await NormalizeAsync("this is definitely not an image"u8.ToArray());

        AssertFailure(result, "Image.Undecodable");
    }

    // R5
    /// <summary>
    /// A PNG whose HEADER declares a 1.6-gigapixel frame while the file itself is a few dozen bytes:
    /// the decompression bomb ADR-045's compressed-size cap cannot see. It must be refused from the
    /// declared dimensions; decoding it would allocate the buffer the check exists to prevent.
    /// </summary>
    [Fact]
    public async Task NormalizeToSquareJpeg_WithADeclaredPixelBomb_FailsAsTooLarge()
    {
        var png = ImageFixtures.CreatePngWithDeclaredSize(40_000, 40_000);

        var result = await NormalizeAsync(png);

        AssertFailure(result, "Image.TooLarge");
    }

    // R6
    /// <summary>Inside the pixel-area ceiling but past the per-edge one: a strip is still pathological.</summary>
    [Fact]
    public async Task NormalizeToSquareJpeg_WithAnOversizedSingleDimension_FailsAsTooLarge()
    {
        var png = ImageFixtures.CreatePngWithDeclaredSize(SkiaSharpImageProcessor.MaxDecodedDimension + 1, 2);

        var result = await NormalizeAsync(png);

        AssertFailure(result, "Image.TooLarge");
    }

    // R7
    [Fact]
    public async Task NormalizeToSquareJpeg_WithAJpegImage_Succeeds()
    {
        var result = await NormalizeAsync(ImageFixtures.CreateSolid(SKEncodedImageFormat.Jpeg, 32, 32, ImageFixtures.Solid));

        AssertSquareJpeg(result);
    }

    // R7
    [Fact]
    public async Task NormalizeToSquareJpeg_WithAPngImage_Succeeds()
    {
        var result = await NormalizeAsync(ImageFixtures.CreateSolid(SKEncodedImageFormat.Png, 32, 32, ImageFixtures.Solid));

        AssertSquareJpeg(result);
    }

    // R7
    [Fact]
    public async Task NormalizeToSquareJpeg_WithAWebpImage_Succeeds()
    {
        var result = await NormalizeAsync(ImageFixtures.CreateSolid(SKEncodedImageFormat.Webp, 32, 32, ImageFixtures.Solid));

        AssertSquareJpeg(result);
    }

    // R7
    [Fact]
    public async Task NormalizeToSquareJpeg_WithAGifImage_Succeeds()
    {
        var result = await NormalizeAsync(ImageFixtures.CreateGif(32, 32, ImageFixtures.GifSolid));

        AssertSquareJpeg(result);
    }

    // R8
    /// <summary>
    /// SkiaSharp decodes BMP, so refusing it proves the format allowlist (JPEG, PNG, WebP, GIF) is
    /// enforced by the processor rather than inherited from whatever the decoder happens to support.
    /// </summary>
    [Fact]
    public async Task NormalizeToSquareJpeg_WithABmpImage_FailsAsUndecodable()
    {
        var result = await NormalizeAsync(ImageFixtures.CreateBmp(32, 32, ImageFixtures.Solid));

        AssertFailure(result, "Image.Undecodable");
    }

    // R8
    /// <summary>SkiaSharp decodes ICO (here wrapping a PNG), so this too is the allowlist at work.</summary>
    [Fact]
    public async Task NormalizeToSquareJpeg_WithAnIcoImage_FailsAsUndecodable()
    {
        var result = await NormalizeAsync(ImageFixtures.CreateIco(32, ImageFixtures.Solid));

        AssertFailure(result, "Image.Undecodable");
    }

    // R8
    [Fact]
    public async Task NormalizeToSquareJpeg_WithATiffImage_FailsAsUndecodable()
    {
        var result = await NormalizeAsync(ImageFixtures.CreateTiff(32, 32, 0x80));

        AssertFailure(result, "Image.Undecodable");
    }

    // R9
    /// <summary>
    /// EXIF (with a GPS IFD: location is PII), XMP and IPTC all ride in JPEG APPn segments; only
    /// pixels may survive the re-encode, so none of the three segment signatures can appear.
    /// </summary>
    [Fact]
    public async Task NormalizeToSquareJpeg_WithExifXmpAndIptcMetadata_StripsAllOfIt()
    {
        var jpeg = ImageFixtures.InsertAfterSoi(
            ImageFixtures.CreateSolid(SKEncodedImageFormat.Jpeg, 300, 300, ImageFixtures.Solid),
            ImageFixtures.CreateExifSegment(ImageFixtures.OrientationTopLeft),
            ImageFixtures.CreateXmpSegment(),
            ImageFixtures.CreateIptcSegment());
        ContainsBytes(jpeg, "Exif\0\0"u8).Should().BeTrue("the fixture must carry the EXIF segment");
        ContainsBytes(jpeg, "http://ns.adobe.com/xap/1.0/"u8).Should().BeTrue("the fixture must carry the XMP segment");
        ContainsBytes(jpeg, "Photoshop 3.0"u8).Should().BeTrue("the fixture must carry the IPTC segment");

        var result = await NormalizeAsync(jpeg);

        AssertSquareJpeg(result);
        ContainsBytes(result.Value!, "Exif\0\0"u8).Should().BeFalse("EXIF (including GPS) must be stripped");
        ContainsBytes(result.Value!, "http://ns.adobe.com/xap/1.0/"u8).Should().BeFalse("XMP must be stripped");
        ContainsBytes(result.Value!, "Photoshop 3.0"u8).Should().BeFalse("IPTC must be stripped");
    }

    // R10
    /// <summary>
    /// A 200x100 image stored left red / right blue, tagged "rotate 90 degrees clockwise to display",
    /// DISPLAYS as 100x200 with red on top. Orientation applied before the crop keeps that: top half
    /// red, bottom half blue. Ignoring it would put the red/blue boundary down the vertical middle.
    /// </summary>
    [Fact]
    public async Task NormalizeToSquareJpeg_WithExifOrientation6_RotatesBeforeCropping()
    {
        var jpeg = CreateLeftRedRightBlueJpeg(ImageFixtures.OrientationRightTop);

        var result = await NormalizeAsync(jpeg);

        AssertSquareJpeg(result);
        using var output = SKBitmap.Decode(result.Value);
        AssertColor(output, Size / 2, Size / 4, ImageFixtures.Red, "top center");
        AssertColor(output, Size / 4, Size / 4, ImageFixtures.Red, "top left quarter");
        AssertColor(output, 3 * Size / 4, Size / 4, ImageFixtures.Red, "top right quarter");
        AssertColor(output, Size / 2, 3 * Size / 4, ImageFixtures.Blue, "bottom center");
        AssertColor(output, Size / 4, 3 * Size / 4, ImageFixtures.Blue, "bottom left quarter");
        AssertColor(output, 3 * Size / 4, 3 * Size / 4, ImageFixtures.Blue, "bottom right quarter");
    }

    // R10 (control)
    [Fact]
    public async Task NormalizeToSquareJpeg_WithExifOrientation1_KeepsTheStoredLayout()
    {
        var jpeg = CreateLeftRedRightBlueJpeg(ImageFixtures.OrientationTopLeft);

        var result = await NormalizeAsync(jpeg);

        AssertSquareJpeg(result);
        using var output = SKBitmap.Decode(result.Value);
        AssertColor(output, Size / 4, Size / 2, ImageFixtures.Red, "middle left");
        AssertColor(output, Size / 4, Size / 4, ImageFixtures.Red, "upper left");
        AssertColor(output, 3 * Size / 4, Size / 2, ImageFixtures.Blue, "middle right");
        AssertColor(output, 3 * Size / 4, 3 * Size / 4, ImageFixtures.Blue, "lower right");
    }

    // R11
    /// <summary>
    /// 640x480 with 80-pixel green bands at both sides: the centered 480x480 square is entirely red.
    /// Cover-cropping shows red to the very edges; stretching would pull the green bands in, and
    /// letterboxing would add padding at the top and bottom corners.
    /// </summary>
    [Fact]
    public async Task NormalizeToSquareJpeg_WithAWiderThanSquareImage_CenterCropsToCover()
    {
        var png = ImageFixtures.Create(
            SKEncodedImageFormat.Png,
            640,
            480,
            ImageFixtures.Red,
            (new SKRect(0, 0, 80, 480), ImageFixtures.Green),
            (new SKRect(560, 0, 640, 480), ImageFixtures.Green));

        var result = await NormalizeAsync(png);

        AssertSquareJpeg(result);
        using var output = SKBitmap.Decode(result.Value);
        AssertColor(output, 2, Size / 2, ImageFixtures.Red, "left edge");
        AssertColor(output, Size - 3, Size / 2, ImageFixtures.Red, "right edge");
        AssertColor(output, 2, 2, ImageFixtures.Red, "top-left corner");
        AssertColor(output, Size - 3, 2, ImageFixtures.Red, "top-right corner");
        AssertColor(output, 2, Size - 3, ImageFixtures.Red, "bottom-left corner");
        AssertColor(output, Size - 3, Size - 3, ImageFixtures.Red, "bottom-right corner");
    }

    // R12
    [Fact]
    public async Task NormalizeToSquareJpeg_WithAMultiFrameGif_UsesOnlyTheFirstFrame()
    {
        var gif = ImageFixtures.CreateGif(64, 64, ImageFixtures.GifRed, ImageFixtures.GifBlue);

        var result = await NormalizeAsync(gif);

        AssertSquareJpeg(result);
        using var output = SKBitmap.Decode(result.Value);
        AssertColor(output, Size / 2, Size / 2, ImageFixtures.Red, "center (first frame)");
    }

    // R12
    [Fact]
    public async Task NormalizeToSquareJpeg_WithAFortyFrameGif_Succeeds()
    {
        var frames = Enumerable.Range(0, 40).Select(i => i % 2 == 0 ? ImageFixtures.GifRed : ImageFixtures.GifBlue).ToArray();
        var gif = ImageFixtures.CreateGif(256, 256, frames);

        var result = await NormalizeAsync(gif);

        AssertSquareJpeg(result);
    }

    // R13
    /// <summary>Uploads arrive as request-body streams, which cannot seek.</summary>
    [Fact]
    public async Task NormalizeToSquareJpeg_WithANonSeekableStream_Succeeds()
    {
        var png = ImageFixtures.CreateSolid(SKEncodedImageFormat.Png, 640, 480, ImageFixtures.Solid);
        await using var input = new NonSeekableStream(png);

        var result = await _sut.NormalizeToSquareJpegAsync(input, Size, TestContext.Current.CancellationToken);

        AssertSquareJpeg(result);
    }

    // R14
    [Fact]
    public async Task NormalizeToSquareJpeg_WithNullContent_ThrowsArgumentNullException()
    {
        var act = () => _sut.NormalizeToSquareJpegAsync(null!, Size, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // R15
    [Fact]
    public async Task NormalizeToSquareJpeg_WithACancelledToken_ThrowsOperationCanceledException()
    {
        var png = ImageFixtures.CreateSolid(SKEncodedImageFormat.Png, 64, 64, ImageFixtures.Solid);
        await using var input = new MemoryStream(png);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var act = () => _sut.NormalizeToSquareJpegAsync(input, Size, cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // R16
    /// <summary>
    /// The headers parse, so the decoder starts; the scan ends halfway. A partially decoded image
    /// (the rest left blank) is not a successful upload.
    /// </summary>
    [Fact]
    public async Task NormalizeToSquareJpeg_WithATruncatedJpeg_FailsAsUndecodable()
    {
        var jpeg = ImageFixtures.CreateBusyJpeg(256, 256);

        var result = await NormalizeAsync(jpeg[..(jpeg.Length / 2)]);

        AssertFailure(result, "Image.Undecodable");
    }

    // R17
    [Fact]
    public void NormalizeToSquareJpeg_DecodeCeilings_KeepTheirValues()
    {
        SkiaSharpImageProcessor.MaxDecodedPixels.Should().Be(50_000_000L);
        SkiaSharpImageProcessor.MaxDecodedDimension.Should().Be(20_000);
    }

    private static byte[] CreateLeftRedRightBlueJpeg(ushort orientation) =>
        ImageFixtures.InsertAfterSoi(
            ImageFixtures.Create(
                SKEncodedImageFormat.Jpeg,
                200,
                100,
                ImageFixtures.Red,
                (new SKRect(100, 0, 200, 100), ImageFixtures.Blue)),
            ImageFixtures.CreateExifSegment(orientation));

    private static void AssertSquareJpeg(Result<byte[]> result)
    {
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNullOrEmpty();
        using var data = SKData.CreateCopy(result.Value);
        using var codec = SKCodec.Create(data);
        codec.Should().NotBeNull("the output must decode");
        codec.EncodedFormat.Should().Be(SKEncodedImageFormat.Jpeg);
        codec.Info.Width.Should().Be(Size);
        codec.Info.Height.Should().Be(Size);
    }

    private static void AssertFailure(Result<byte[]> result, string code)
    {
        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e => e.Code == code);
    }

    /// <summary>JPEG is lossy, so a pixel matches when every channel is within 60 of the expected one.</summary>
    private static void AssertColor(SKBitmap bitmap, int x, int y, SKColor expected, string where)
    {
        var actual = bitmap.GetPixel(x, y);
        var distance = Math.Max(
            Math.Abs(actual.Red - expected.Red),
            Math.Max(Math.Abs(actual.Green - expected.Green), Math.Abs(actual.Blue - expected.Blue)));
        distance.Should().BeLessThan(60, "the {0} pixel at ({1}, {2}) should be {3} but was {4}", where, x, y, expected, actual);
    }

    private static bool ContainsBytes(byte[] haystack, ReadOnlySpan<byte> needle) => haystack.AsSpan().IndexOf(needle) >= 0;

    private async Task<Result<byte[]>> NormalizeAsync(byte[] bytes)
    {
        await using var input = new MemoryStream(bytes);
        return await _sut.NormalizeToSquareJpegAsync(input, Size, TestContext.Current.CancellationToken);
    }

    /// <summary>A read-only stream that cannot seek and does not know its length, like a request body.</summary>
    private sealed class NonSeekableStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream _inner = new(bytes);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override int Read(Span<byte> buffer) => _inner.Read(buffer);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            _inner.ReadAsync(buffer, offset, count, cancellationToken);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}

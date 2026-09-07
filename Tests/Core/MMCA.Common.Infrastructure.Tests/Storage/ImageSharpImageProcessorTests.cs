using AwesomeAssertions;
using MMCA.Common.Infrastructure.Storage;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;

namespace MMCA.Common.Infrastructure.Tests.Storage;

/// <summary>
/// Tests for <see cref="ImageSharpImageProcessor"/> (ADR-045): untrusted uploads come out as
/// exact-size square JPEGs with ALL metadata stripped (EXIF GPS is PII), and undecodable
/// content fails as a validation error rather than an exception.
/// </summary>
public sealed class ImageSharpImageProcessorTests
{
    private readonly ImageSharpImageProcessor _sut = new();

    private static async Task<MemoryStream> CreatePngAsync(int width, int height, bool withExif = false)
    {
        using var image = new Image<Rgba32>(width, height, new Rgba32(200, 30, 30));
        if (withExif)
        {
            var exif = new ExifProfile();
            exif.SetValue(ExifTag.GPSLatitudeRef, "N");
            exif.SetValue(ExifTag.Artist, "Test Artist");
            image.Metadata.ExifProfile = exif;
        }

        var stream = new MemoryStream();
        await image.SaveAsync(stream, new PngEncoder(), TestContext.Current.CancellationToken);
        stream.Position = 0;
        return stream;
    }

    [Fact]
    public async Task NormalizeToSquareJpeg_ProducesExactSquareJpeg()
    {
        await using var input = await CreatePngAsync(640, 480);

        var result = await _sut.NormalizeToSquareJpegAsync(input, 256, TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        using var output = Image.Load(result.Value!);
        output.Width.Should().Be(256);
        output.Height.Should().Be(256);
        Image.DetectFormat(result.Value!).Name.Should().Be("JPEG");
    }

    [Fact]
    public async Task NormalizeToSquareJpeg_StripsExifMetadata()
    {
        await using var input = await CreatePngAsync(300, 300, withExif: true);

        var result = await _sut.NormalizeToSquareJpegAsync(input, 256, TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        using var output = Image.Load(result.Value!);
        output.Metadata.ExifProfile.Should().BeNull();
        output.Metadata.XmpProfile.Should().BeNull();
        output.Metadata.IptcProfile.Should().BeNull();
    }

    [Fact]
    public async Task NormalizeToSquareJpeg_UpscalesSmallImagesToTheRequestedSize()
    {
        await using var input = await CreatePngAsync(64, 48);

        var result = await _sut.NormalizeToSquareJpegAsync(input, 256, TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        using var output = Image.Load(result.Value!);
        output.Width.Should().Be(256);
        output.Height.Should().Be(256);
    }

    [Fact]
    public async Task NormalizeToSquareJpeg_WithNonImageContent_FailsValidation()
    {
        await using var input = new MemoryStream("this is definitely not an image"u8.ToArray());

        var result = await _sut.NormalizeToSquareJpegAsync(input, 256, TestContext.Current.CancellationToken);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e => e.Code == "Image.Undecodable");
    }

    // ── SEC-Common-28: pixel and dimension ceiling ──
    /// <summary>
    /// A PNG whose HEADER declares an enormous frame while the file itself stays small: exactly the
    /// decompression bomb ADR-045's 2 MB compressed cap cannot see. The header is rewritten rather
    /// than a real image encoded, because encoding one would cost the allocation under test.
    /// </summary>
    private static MemoryStream CreatePngWithDeclaredSize(int width, int height)
    {
        using var small = new Image<Rgba32>(4, 4, new Rgba32(1, 2, 3));
        using var buffer = new MemoryStream();
        small.Save(buffer, new PngEncoder());
        var bytes = buffer.ToArray();

        // PNG layout: 8-byte signature, then the IHDR chunk (4-byte length, 4-byte type, then
        // width and height as big-endian uint32). Rewrite the two dimensions and the chunk CRC.
        const int ihdrDataOffset = 16;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(ihdrDataOffset, 4), (uint)width);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(ihdrDataOffset + 4, 4), (uint)height);

        var crc = System.IO.Hashing.Crc32.HashToUInt32(bytes.AsSpan(12, 17));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(29, 4), crc);

        return new MemoryStream(bytes);
    }

    [Fact]
    public async Task NormalizeToSquareJpeg_WithADeclaredPixelBomb_FailsValidationWithoutDecoding()
    {
        await using var input = CreatePngWithDeclaredSize(40_000, 40_000);

        var result = await _sut.NormalizeToSquareJpegAsync(input, 256, TestContext.Current.CancellationToken);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e => e.Code == "Image.TooLarge");
    }

    [Fact]
    public async Task NormalizeToSquareJpeg_WithAnOversizedSingleDimension_FailsValidation()
    {
        // Inside the 50 MP area ceiling, past the per-edge one: a strip is still pathological.
        await using var input = CreatePngWithDeclaredSize(ImageSharpImageProcessor.MaxDecodedDimension + 1, 2);

        var result = await _sut.NormalizeToSquareJpegAsync(input, 256, TestContext.Current.CancellationToken);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e => e.Code == "Image.TooLarge");
    }

    [Fact]
    public async Task NormalizeToSquareJpeg_StillAcceptsARealCameraSizedImage()
    {
        await using var input = await CreatePngAsync(4000, 3000);

        var result = await _sut.NormalizeToSquareJpegAsync(input, 256, TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
    }
}

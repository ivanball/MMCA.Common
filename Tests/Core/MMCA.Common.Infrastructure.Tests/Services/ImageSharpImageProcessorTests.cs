using AwesomeAssertions;
using MMCA.Common.Infrastructure.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;

namespace MMCA.Common.Infrastructure.Tests.Services;

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
}

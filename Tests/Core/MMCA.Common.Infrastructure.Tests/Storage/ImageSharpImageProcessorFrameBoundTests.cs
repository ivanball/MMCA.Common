using System.Buffers;
using System.Runtime.CompilerServices;
using AwesomeAssertions;
using MMCA.Common.Infrastructure.Storage;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Memory;
using SixLabors.ImageSharp.PixelFormats;
using ImageSharpConfiguration = SixLabors.ImageSharp.Configuration;

namespace MMCA.Common.Infrastructure.Tests.Storage;

/// <summary>
/// The normalizer uses only the first frame, so it must decode only the first frame (L92): an
/// animated upload that is small on disk otherwise expands into one full RGBA buffer per frame.
/// </summary>
/// <remarks>
/// Measured through a counting <see cref="MemoryAllocator"/> installed on
/// <see cref="ImageSharpConfiguration.Default"/>, because ImageSharp takes pixel buffers from its own pooled
/// and unmanaged memory, which the GC allocation counter never sees. The allocator is process-wide,
/// so the class runs in a collection that never runs in parallel with any other test, and the
/// original allocator is always restored.
/// </remarks>
[Collection(ImageFrameBoundCollection.Name)]
public sealed class ImageSharpImageProcessorFrameBoundTests
{
    private const int Side = 1024;
    private const int FrameCount = 40;

    [Fact]
    public async Task NormalizeToSquareJpegAsync_WithAManyFrameGif_DecodesOnlyTheFirstFrame()
    {
        await using var gif = await CreateManyFrameGifAsync();
        var sut = new ImageSharpImageProcessor();

        var original = ImageSharpConfiguration.Default.MemoryAllocator;
        var counting = new CountingAllocator(original);
        ImageSharpConfiguration.Default.MemoryAllocator = counting;
        try
        {
            var result = await sut.NormalizeToSquareJpegAsync(gif, 64, TestContext.Current.CancellationToken);

            result.IsSuccess.Should().BeTrue();
        }
        finally
        {
            ImageSharpConfiguration.Default.MemoryAllocator = original;
        }

        counting.AllocatedBytes.Should().BeLessThan(
            64L * 1024 * 1024,
            "one RGBA frame of 1024x1024 is 4 MB; decoding all forty frames would take about 160 MB");
    }

    private static async Task<MemoryStream> CreateManyFrameGifAsync()
    {
        using var image = new Image<Rgba32>(Side, Side, new Rgba32(10, 20, 30));
        for (var i = 1; i < FrameCount; i++)
        {
            using var frame = new Image<Rgba32>(Side, Side, i % 2 == 0 ? new Rgba32(10, 20, 30) : new Rgba32(200, 30, 30));
            image.Frames.AddFrame(frame.Frames.RootFrame);
        }

        var stream = new MemoryStream();
        await image.SaveAsync(stream, new GifEncoder(), TestContext.Current.CancellationToken);
        stream.Position = 0;
        return stream;
    }

    /// <summary>Delegates every allocation to the real allocator and totals the bytes requested.</summary>
    private sealed class CountingAllocator(MemoryAllocator inner) : MemoryAllocator
    {
        private long _allocatedBytes;

        public long AllocatedBytes => Interlocked.Read(ref _allocatedBytes);

        public override IMemoryOwner<T> Allocate<T>(int length, AllocationOptions options = AllocationOptions.None)
        {
            Interlocked.Add(ref _allocatedBytes, (long)length * Unsafe.SizeOf<T>());
            return inner.Allocate<T>(length, options);
        }

        public override void ReleaseRetainedResources() => inner.ReleaseRetainedResources();

        protected override int GetBufferCapacityInBytes() => 4 * 1024 * 1024;
    }
}

/// <summary>Runs the allocator-swapping test alone, with no other test class in parallel.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ImageFrameBoundCollection
{
    /// <summary>The collection name.</summary>
    public const string Name = "ImageFrameBound";
}

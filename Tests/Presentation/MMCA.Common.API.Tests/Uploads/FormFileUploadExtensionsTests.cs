using System.Text;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using MMCA.Common.API.Uploads;
using MMCA.Common.Shared.Abstractions;
using Moq;

namespace MMCA.Common.API.Tests.Uploads;

/// <summary>
/// The bounded upload read: an argument check up front, the caller's own error for a missing, empty,
/// oversize or truncated file, and on success the bytes, file name and content type exactly as declared.
/// </summary>
public sealed class FormFileUploadExtensionsTests
{
    private const long MaxBytes = 16;

    private static readonly Error InvalidUpload = Error.Validation(
        code: "Avatar.InvalidUpload",
        message: "Provide one image file of at most 16 bytes.",
        source: "UsersController",
        target: "file");

    // -- Rule 2: the caller's error, unchanged, for every refused file --
    [Fact]
    public async Task NullFile_ReturnsCallerError()
    {
        IFormFile? file = null;

        Result<UploadedFileContent> result = await file.ReadBoundedAsync(MaxBytes, InvalidUpload, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle().Which.Should().Be(
            InvalidUpload,
            "the caller's code, message, source and target travel unchanged");
    }

    [Fact]
    public async Task EmptyFile_ReturnsCallerError()
    {
        IFormFile file = CreateFormFile([]);

        Result<UploadedFileContent> result = await file.ReadBoundedAsync(MaxBytes, InvalidUpload, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle().Which.Should().Be(InvalidUpload);
    }

    [Fact]
    public async Task OversizeFile_ReturnsCallerError()
    {
        var tracked = new TrackingStream(new byte[MaxBytes + 1]);
        IFormFile file = CreateTrackedFile(tracked, length: MaxBytes + 1);

        Result<UploadedFileContent> result = await file.ReadBoundedAsync(MaxBytes, InvalidUpload, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle().Which.Should().Be(InvalidUpload);
        tracked.BytesRead.Should().Be(0, "a declared length over the cap is refused before any byte is read");
    }

    // -- Rules 3 and 5: one exact read, the declared name and type kept --
    [Fact]
    public async Task ExactlyMaxBytes_Succeeds()
    {
        byte[] bytes = [.. Enumerable.Range(0, (int)MaxBytes).Select(i => (byte)i)];
        IFormFile file = CreateFormFile(bytes);

        Result<UploadedFileContent> result = await file.ReadBoundedAsync(MaxBytes, InvalidUpload, CancellationToken.None);

        result.IsSuccess.Should().BeTrue("the cap is inclusive: a file of exactly maxBytes is accepted");
        result.Value!.Content.Should().Equal(bytes);
    }

    [Fact]
    public async Task Success_CarriesBytesNameAndContentType()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("hello");
        IFormFile file = CreateFormFile(bytes, fileName: "avatar.PNG", contentType: "image/png");

        Result<UploadedFileContent> result = await file.ReadBoundedAsync(MaxBytes, InvalidUpload, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        UploadedFileContent content = result.Value!;
        content.Content.Should().Equal(bytes);
        content.Content.Should().HaveCount((int)file.Length, "one buffer of the declared length is filled");
        content.FileName.Should().Be("avatar.PNG", "the file name is carried as declared, not normalized");
        content.ContentType.Should().Be("image/png");
    }

    [Fact]
    public async Task Success_DisposesTheUploadStream()
    {
        var tracked = new TrackingStream(Encoding.UTF8.GetBytes("abc"));
        IFormFile file = CreateTrackedFile(tracked, length: 3);

        Result<UploadedFileContent> result = await file.ReadBoundedAsync(MaxBytes, InvalidUpload, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        tracked.IsDisposed.Should().BeTrue("the stream opened for the read is disposed by the helper");
    }

    // -- Rule 4: a stream shorter than its declared length --
    [Fact]
    public async Task ShortStream_ReturnsCallerError()
    {
        var tracked = new TrackingStream(Encoding.UTF8.GetBytes("abc"));
        IFormFile file = CreateTrackedFile(tracked, length: 10);

        Result<UploadedFileContent> result = await file.ReadBoundedAsync(MaxBytes, InvalidUpload, CancellationToken.None);

        result.IsFailure.Should().BeTrue("a truncated upload is refused, not returned zero-padded");
        result.Errors.Should().ContainSingle().Which.Should().Be(InvalidUpload);
    }

    // -- Rule 1: argument checks throw --
    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData((long)int.MaxValue + 1)]
    public async Task MaxBytesOutOfRange_Throws(long maxBytes)
    {
        // int.MaxValue + 1 is above Array.MaxLength on every runtime, so it stands for "above Array.MaxLength".
        IFormFile file = CreateFormFile([1, 2, 3]);

        Func<Task> act = () => file.ReadBoundedAsync(maxBytes, InvalidUpload, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task MaxBytesOfArrayMaxLength_IsAccepted()
    {
        IFormFile file = CreateFormFile([1, 2, 3]);

        Result<UploadedFileContent> result = await file.ReadBoundedAsync(Array.MaxLength, InvalidUpload, CancellationToken.None);

        result.IsSuccess.Should().BeTrue("Array.MaxLength itself is the largest cap a single buffer can honor");
    }

    [Fact]
    public async Task NullError_Throws()
    {
        IFormFile file = CreateFormFile([1, 2, 3]);

        Func<Task> act = () => file.ReadBoundedAsync(MaxBytes, null!, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // -- Rule 6: cancellation propagates --
    [Fact]
    public async Task Cancelled_Throws()
    {
        IFormFile file = CreateFormFile(Encoding.UTF8.GetBytes("payload"));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        Func<Task> act = () => file.ReadBoundedAsync(MaxBytes, InvalidUpload, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>("cancellation is not turned into the caller's error");
    }

    // -- Helpers --
    internal static IFormFile CreateFormFile(byte[] bytes, string fileName = "upload.bin", string contentType = "application/octet-stream")
    {
        var stream = new MemoryStream(bytes);
        return new FormFile(stream, 0, bytes.Length, "file", fileName)
        {
            Headers = new HeaderDictionary(),
            ContentType = contentType,
        };
    }

    private static IFormFile CreateTrackedFile(TrackingStream stream, long length)
    {
        var file = new Mock<IFormFile>();
        file.SetupGet(f => f.Length).Returns(length);
        file.SetupGet(f => f.FileName).Returns("upload.bin");
        file.SetupGet(f => f.Name).Returns("file");
        file.SetupGet(f => f.ContentType).Returns("application/octet-stream");
        file.Setup(f => f.OpenReadStream()).Returns(stream);
        file.Setup(f => f.CopyToAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .Returns<Stream, CancellationToken>((target, token) => stream.CopyToAsync(target, token));
        return file.Object;
    }

    /// <summary>A memory stream that records whether it was disposed and how many bytes were read.</summary>
    private sealed class TrackingStream(byte[] bytes) : MemoryStream(bytes)
    {
        public bool IsDisposed { get; private set; }

        public long BytesRead { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = base.Read(buffer, offset, count);
            BytesRead += read;
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            int read = base.Read(buffer);
            BytesRead += read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int read = await base.ReadAsync(buffer, cancellationToken);
            BytesRead += read;
            return read;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            int read = await base.ReadAsync(buffer.AsMemory(offset, count), cancellationToken);
            BytesRead += read;
            return read;
        }

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }
    }
}

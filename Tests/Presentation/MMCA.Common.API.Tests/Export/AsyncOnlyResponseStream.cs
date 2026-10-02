namespace MMCA.Common.API.Tests.Export;

/// <summary>
/// A response body that behaves like Kestrel's default: every synchronous write or flush throws,
/// only the awaited overloads work. Whatever is written asynchronously is kept for assertions.
/// </summary>
internal sealed class AsyncOnlyResponseStream : Stream
{
    private readonly MemoryStream _inner = new();

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => _inner.Length;

    public override long Position
    {
        get => _inner.Position;
        set => throw new NotSupportedException();
    }

    public byte[] ToArray() => _inner.ToArray();

    public override void Flush() => throw SynchronousIoDisallowed();

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override void Write(byte[] buffer, int offset, int count) => throw SynchronousIoDisallowed();

    public override void Write(ReadOnlySpan<byte> buffer) => throw SynchronousIoDisallowed();

    public override void WriteByte(byte value) => throw SynchronousIoDisallowed();

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        _inner.WriteAsync(buffer, offset, count, cancellationToken);

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
        _inner.WriteAsync(buffer, cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }

    private static InvalidOperationException SynchronousIoDisallowed() =>
        new("Synchronous operations are disallowed. Call WriteAsync or set AllowSynchronousIO to true instead.");
}

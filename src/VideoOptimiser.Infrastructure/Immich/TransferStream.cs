namespace VideoOptimiser.Infrastructure.Immich;

/// <summary>
/// A read-only pass-through for an Immich download or upload. Every read re-arms the idle timer, so a slow but moving
/// transfer of a multi-GB file never times out while a stalled one does, and reports the running byte count.
/// </summary>
internal sealed class TransferStream(Stream inner, CancellationTokenSource idleTimer, TimeSpan idleTimeout, IProgress<long>? progress) : Stream
{
    public long Transferred { get; private set; }

    public override bool CanRead => true;
    // Seeking passes through so StreamContent can compute Content-Length for an upload instead of sending it chunked.
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => inner.Position = value;
    }

    public override int Read(byte[] buffer, int offset, int count) => Advance(inner.Read(buffer, offset, count));

    public override int Read(Span<byte> buffer) => Advance(inner.Read(buffer));

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        Advance(await inner.ReadAsync(buffer, cancellationToken));

    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync();
        await base.DisposeAsync();
    }

    private int Advance(int read)
    {
        if (read > 0)
        {
            idleTimer.CancelAfter(idleTimeout);
            Transferred += read;
            progress?.Report(Transferred);
        }

        return read;
    }
}

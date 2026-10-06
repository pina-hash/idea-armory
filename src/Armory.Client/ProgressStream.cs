namespace Armory.Client;

// A read-only view of a stream that reports the running total of bytes read through it. A seek
// (StreamContent rewinds its stream when a request body is sent again) starts the count again
// from 0, so the total always describes the current attempt. It never disposes the inner stream;
// the caller that opened it does.
internal sealed class ProgressStream(Stream inner, IProgress<long> progress) : Stream
{
    private long _total;

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => inner.Length;
    public override long Position
    {
        get => inner.Position;
        set { inner.Position = value; Restart(); }
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        var position = inner.Seek(offset, origin);
        Restart();
        return position;
    }

    public override int Read(byte[] buffer, int offset, int count) => Counted(inner.Read(buffer, offset, count));
    public override int Read(Span<byte> buffer) => Counted(inner.Read(buffer));
    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => Counted(await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken));
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => Counted(await inner.ReadAsync(buffer, cancellationToken));

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    private void Restart()
    {
        _total = 0;
        progress.Report(0);
    }

    private int Counted(int read)
    {
        if (read > 0)
        {
            _total += read;
            progress.Report(_total);
        }
        return read;
    }
}

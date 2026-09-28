using Armory.Core;

namespace Armory.Core.Tests;

public sealed class ContentTests
{
    [Fact]
    public async Task Identical_bytes_match_and_one_byte_changes_address()
    {
        var a = await ContentAddress.ComputeAsync(new MemoryStream([1, 2, 3]));
        var b = await ContentAddress.ComputeAsync(new MemoryStream([1, 2, 3]));
        var c = await ContentAddress.ComputeAsync(new MemoryStream([1, 2, 4]));
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.Matches("^[0-9a-f]{64}$", a);
        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", await ContentAddress.ComputeAsync(new MemoryStream()));
    }

    [Fact]
    public async Task Hashes_64_MiB_from_nonseekable_generated_stream_with_bounded_reads()
    {
        using var stream = new GeneratedStream(64L * 1024 * 1024);
        var result = await ContentAddress.ComputeAsync(stream);
        Assert.Equal(64, result.Length);
        Assert.Equal(64L * 1024 * 1024, stream.ReadBytes);
        Assert.InRange(stream.MaxRequested, 1, 128 * 1024);
        Assert.True(stream.CanRead);
    }

    [Fact]
    public async Task Cancellation_is_honored()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await ContentAddress.ComputeAsync(new GeneratedStream(100), cancellation.Token));
    }

    private sealed class GeneratedStream(long length) : Stream
    {
        public long ReadBytes { get; private set; }
        public int MaxRequested { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            MaxRequested = Math.Max(MaxRequested, buffer.Length);
            var count = (int)Math.Min(buffer.Length, length - ReadBytes);
            buffer[..count].Fill(0x5a);
            ReadBytes += count;
            return count;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(Read(buffer.Span)); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

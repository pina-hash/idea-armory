using System.Text;
using Armory.Core;

namespace Armory.Core.Tests;

// The saved-release reader (docs/core/solidworks-version-gate.md). Every file here is a
// synthetic container built in code by SwContainer, with the layout measured on 158 public
// files (research saved-release-and-savedown.md section 1.1). No CAD file is read or committed.
public sealed class SolidWorksFileReleaseTests
{
    private static SolidWorksRelease? Read(byte[] bytes) => SolidWorksFileRelease.Read(new MemoryStream(bytes));

    // A file last saved in release `code`, saved before in the releases listed first.
    private static SwContainer SavedIn(int code, params int[] earlier) => SwContainer.Typical(code, [.. earlier, code]);

    [Theory]
    [InlineData(8000, 2015)][InlineData(9000, 2016)][InlineData(10000, 2017)][InlineData(11000, 2018)]
    [InlineData(12000, 2019)][InlineData(13000, 2020)][InlineData(14000, 2021)][InlineData(15000, 2022)]
    [InlineData(16000, 2023)][InlineData(17000, 2024)][InlineData(18000, 2025)][InlineData(19000, 2026)]
    public void Every_code_in_the_official_table_reads_as_its_release(int code, int year)
    {
        Assert.Equal(year, SolidWorksFileRelease.YearOf(code));
        Assert.Equal(new SolidWorksRelease(year), Read(SavedIn(code).Build()));
        // Saved before in older releases, as most files are: the last entry is what counts.
        Assert.Equal(new SolidWorksRelease(year), Read(SavedIn(code, 3100, 4400, 7000).Build()));
    }

    [Fact]
    public void A_code_beyond_the_table_follows_one_release_per_thousand()
    {
        Assert.Equal(2027, SolidWorksFileRelease.YearOf(20000));
        Assert.Equal(new SolidWorksRelease(2027), Read(SavedIn(20000, 19000).Build()));
    }

    [Fact]
    public void A_pre_release_code_rounds_up_to_the_release_it_previews()
    {
        // A 2026 beta (18800) can never read as 2025.
        Assert.Equal(2026, SolidWorksFileRelease.YearOf(18800));
        Assert.Equal(new SolidWorksRelease(2026), Read(SavedIn(18800, 18000).Build()));
        Assert.Equal(2026, SolidWorksFileRelease.YearOf(18001));
        Assert.Equal(2025, SolidWorksFileRelease.YearOf(17001));
        // Below 8000 is no 2015-or-later code, and nothing overflows.
        Assert.Null(SolidWorksFileRelease.YearOf(7999));
        Assert.Null(SolidWorksFileRelease.YearOf(0));
        Assert.Null(SolidWorksFileRelease.YearOf(-18000));
        Assert.Equal(2007 + 2147484, SolidWorksFileRelease.YearOf(int.MaxValue));
        Assert.Null(Read(SavedIn(7000, 6000).Build()));
    }

    // The precision rule: a 2025 file has 18000 in both places and reads 2025 exactly; nothing
    // that disagrees reads as any year.
    [Fact]
    public void A_2025_file_reads_only_as_2025()
    {
        Assert.Equal(new SolidWorksRelease(2025), Read(SavedIn(18000, 17000).Build()));
        // History says 2026 last, the stream names say 2025: unknown, never 2026 or 2025.
        Assert.Null(Read(SwContainer.Typical(18000, [18000, 19000]).Build()));
        Assert.Null(Read(SwContainer.Typical(19000, [19000, 18000, 17000]).Build()));
    }

    [Fact]
    public void The_last_major_code_counts_not_the_largest()
    {
        // A file saved down from 2026 to 2025 may list 19000 before 18000 (research 1.7).
        Assert.Equal(new SolidWorksRelease(2025), Read(SwContainer.Typical(18000, [17000, 19000, 18000]).Build()));
    }

    [Fact]
    public void One_release_code_in_the_stream_names_reads_and_two_are_unknown()
    {
        var one = SavedIn(18000);
        one.Add("_MO_VERSION_18000/Biography", Bytes(4000, 1));
        one.Add("_MO_VERSION_18000/AssyVisualData", Bytes(4, 2));
        Assert.Equal(new SolidWorksRelease(2025), Read(one.Build()));
        var two = SavedIn(18000);
        two.Add("_MO_VERSION_19000/Biography", Bytes(4000, 1));
        Assert.Null(Read(two.Build()));
        // The other code before the history's own, so the last one seen agrees with it.
        var before = new SwContainer();
        before.AddTypicalStreams();
        before.Add("_MO_VERSION_17000/Biography", Bytes(4000, 1));
        before.Add("_MO_VERSION_18000/History", SwContainer.History([17000, 18000]));
        Assert.Null(Read(before.Build()));
        // The display list lags the document and is no release signal.
        var displayList = SavedIn(18000);
        displayList.Add("_DL_VERSION_7000/DLUpdateStamp", Bytes(6, 3));
        Assert.Equal(new SolidWorksRelease(2025), Read(displayList.Build()));
    }

    [Fact]
    public void A_missing_or_duplicated_history_is_unknown()
    {
        var missing = new SwContainer();
        missing.AddTypicalStreams();
        missing.Add("_MO_VERSION_18000/Biography", Bytes(4000, 1));
        Assert.Null(Read(missing.Build()));
        var twice = SavedIn(18000);
        twice.Add("_MO_VERSION_18000/History", SwContainer.History([18000]));
        Assert.Null(Read(twice.Build()));
    }

    [Theory]
    [InlineData("moDateCodeHistory_c", 1)]
    [InlineData("moVersionHistory_d", 1)]
    [InlineData("moVersionHistory_c", 2)]
    public void A_history_of_another_class_is_unknown(string className, int schema)
    {
        var history = SwContainer.History([17000, 18000], className: className, schema: schema);
        Assert.Null(Read(SwContainer.WithHistory(18000, history).Build()));
    }

    [Theory]
    [InlineData(1)][InlineData(3)][InlineData(0)][InlineData(1001)]
    public void A_history_whose_count_is_not_its_entries_is_unknown(int count)
    {
        var history = SwContainer.History([17000, 18000], count: (uint)count);
        Assert.Null(Read(SwContainer.WithHistory(18000, history).Build()));
    }

    [Fact]
    public void A_history_of_more_than_a_thousand_entries_is_unknown()
    {
        var majors = Enumerable.Repeat(18000, 1001).ToArray();
        Assert.Null(Read(SwContainer.WithHistory(18000, SwContainer.History(majors)).Build()));
        var most = Enumerable.Repeat(18000, 1000).ToArray();
        Assert.Equal(new SolidWorksRelease(2025), Read(SwContainer.WithHistory(18000, SwContainer.History(most)).Build()));
    }

    [Fact]
    public void A_history_must_inflate_to_exactly_its_declared_size_of_at_most_one_mebibyte()
    {
        var history = SwContainer.History([17000, 18000]);
        Assert.Null(Read(SwContainer.WithHistory(18000, history, declared: history.Length + 1).Build()));
        Assert.Null(Read(SwContainer.WithHistory(18000, history, declared: history.Length - 1).Build()));
        Assert.Null(Read(SwContainer.WithHistory(18000, history, declared: 0).Build()));
        // Padded past 1 MiB, declared truthfully: still too big to be a history.
        var oversized = history.Concat(new byte[1 << 20]).ToArray();
        Assert.Null(Read(SwContainer.WithHistory(18000, oversized).Build()));
        var justFits = history.Concat(new byte[(1 << 20) - history.Length]).ToArray();
        Assert.Equal(new SolidWorksRelease(2025), Read(SwContainer.WithHistory(18000, justFits).Build()));
    }

    [Fact]
    public void A_history_that_is_not_valid_deflate_is_unknown()
    {
        var history = SwContainer.History([17000, 18000]);
        var container = new SwContainer();
        container.AddTypicalStreams();
        // Bytes that are no raw DEFLATE stream (a reserved block type), declared as the history.
        container.AddRaw("_MO_VERSION_18000/History", [0xFF, 0xFF, 0xFF, 0xFF, 0x07, 0x00, 0x01], history.Length);
        Assert.Null(Read(container.Build()));
        // A valid stream cut short.
        var deflated = SwContainer.Deflate(history);
        var cut = new SwContainer();
        cut.AddTypicalStreams();
        cut.AddRaw("_MO_VERSION_18000/History", deflated[..(deflated.Length / 2)], history.Length);
        Assert.Null(Read(cut.Build()));
    }

    [Fact]
    public void A_truncated_file_is_unknown()
    {
        var bytes = SavedIn(18000, 17000).Build();
        var history = IndexOf(bytes, SwContainer.Rotate("_MO_VERSION_18000/History", 4));
        Assert.True(history > 0);
        Assert.Null(Read(bytes[..(history + 30)])); // inside the history's compressed bytes
        Assert.Null(Read(bytes[..(history - 8)])); // inside its header
        Assert.Null(Read(bytes[..63]));
        Assert.Null(Read([]));
    }

    [Fact]
    public void A_compound_file_from_before_2015_is_unknown()
    {
        var bytes = SavedIn(18000).Build();
        byte[] ole = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];
        ole.CopyTo(bytes, 0);
        Assert.Null(Read(bytes));
    }

    [Fact]
    public void A_file_without_the_marker_in_its_first_64_bytes_is_unknown()
    {
        Assert.Null(Read(new SwContainer { FirstChunkAt = 61 }.WithTypical(18000).Build()));
        Assert.Equal(new SolidWorksRelease(2025), Read(new SwContainer { FirstChunkAt = 54 }.WithTypical(18000).Build()));
        Assert.Null(Read(Encoding.ASCII.GetBytes(new string('x', 4096))));
    }

    [Theory]
    [InlineData(0x04)][InlineData(0x00)][InlineData(0x01)][InlineData(0x03)][InlineData(0x07)][InlineData(0x0C)]
    public void Stream_names_are_rotated_by_the_key_at_byte_7(int keyByte)
    {
        Assert.Equal(new SolidWorksRelease(2026), Read(new SwContainer { KeyByte = (byte)keyByte }.WithTypical(19000).Build()));
        // Names rotated by one key under another key byte: no release stream is found.
        Assert.Null(Read(new SwContainer { KeyByte = (byte)keyByte, NameKey = (keyByte + 3) & 7 }.WithTypical(19000).Build()));
    }

    [Fact]
    public void Table_of_contents_entries_are_skipped()
    {
        var container = SavedIn(18000);
        container.AddToc("_MO_VERSION_19000/History");
        container.AddToc("_MO_VERSION_19000/Biography");
        Assert.Equal(new SolidWorksRelease(2025), Read(container.Build()));
    }

    [Fact]
    public void A_chunk_header_inside_another_stream_is_never_read()
    {
        // A stream whose stored bytes hold what looks like a 2026 history chunk: the walker
        // skips every payload, so it never sees it.
        var inner = new SwContainer { FirstChunkAt = 0, KeyByte = 4 };
        inner.Add("_MO_VERSION_19000/History", SwContainer.History([19000]));
        var container = new SwContainer();
        container.AddTypicalStreams();
        container.AddRaw("Contents/Config-0", inner.Build(), 100);
        container.Add("_MO_VERSION_18000/History", SwContainer.History([17000, 18000]));
        Assert.Equal(new SolidWorksRelease(2025), Read(container.Build()));
    }

    [Theory]
    [InlineData("_MO_VERSION_abc/History")]
    [InlineData("_MO_VERSION_18000")]
    [InlineData("_MO_VERSION_/History")]
    [InlineData("_MO_VERSION_-18000/Biography")]
    public void A_malformed_release_stream_name_is_unknown(string name)
    {
        var container = SavedIn(18000);
        container.Add(name, Bytes(10, 4));
        Assert.Null(Read(container.Build()));
    }

    [Fact]
    public void The_history_is_found_wherever_it_sits()
    {
        var first = new SwContainer();
        first.Add("_MO_VERSION_17000/History", SwContainer.History([16000, 17000]));
        first.AddTypicalStreams();
        first.Add("Contents/Big", Bytes(300_000, 7));
        Assert.Equal(new SolidWorksRelease(2024), Read(first.Build()));
        var last = new SwContainer();
        last.AddTypicalStreams();
        last.Add("Contents/Big", Bytes(300_000, 7));
        last.Add("_MO_VERSION_17000/Biography", Bytes(50_000, 8));
        last.Add("_MO_VERSION_17000/History", SwContainer.History([16000, 17000]));
        Assert.Equal(new SolidWorksRelease(2024), Read(last.Build()));
    }

    [Fact]
    public async Task A_stream_that_cannot_seek_is_unknown_and_nothing_throws()
    {
        var bytes = SavedIn(18000).Build();
        Assert.Null(SolidWorksFileRelease.Read(new ForwardOnly(bytes)));
        Assert.Null(await new SolidWorksSavedReleaseReader().ReadAsync(new ForwardOnly(bytes)));
        Assert.Equal(new SolidWorksRelease(2025), await new SolidWorksSavedReleaseReader().ReadAsync(new MemoryStream(bytes)));
    }

    [Fact]
    public void Reading_can_be_canceled()
    {
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => SolidWorksFileRelease.Read(new MemoryStream(SavedIn(18000).Build()), canceled.Token));
    }

    // Truncations and bit flips (seeded, mirroring the 6,320 trials on real files): the answer
    // is the true year or unknown, never another year, and the reader never throws.
    [Fact]
    public void Truncations_and_bit_flips_never_change_a_known_year_and_never_throw()
    {
        var random = new Random(20261009);
        SwContainer[] files =
        [
            SavedIn(18000, 17000, 16000),
            SavedIn(19000, 18000),
            SavedIn(13000, 3100, 4400, 7000, 10000),
            new SwContainer { KeyByte = 3 }.WithTypical(17000),
            SavedIn(14000).With("Contents/Big", Bytes(80_000, 9)).With("_MO_VERSION_14000/Biography", Bytes(5_000, 10)),
        ];
        var trials = 0;
        foreach (var file in files)
        {
            var bytes = file.Build();
            var truth = Read(bytes);
            Assert.NotNull(truth);
            for (var i = 0; i < 800; i++)
            {
                byte[] input;
                if (i % 2 == 0) input = bytes[..random.Next(0, bytes.Length)];
                else
                {
                    input = (byte[])bytes.Clone();
                    for (var k = 0; k < 1 + random.Next(8); k++) input[random.Next(input.Length)] ^= (byte)(1 << random.Next(8));
                }
                var read = Read(input);
                Assert.True(read is null || read == truth, $"trial {trials}: {truth} read as {read}");
                trials++;
            }
        }
        Assert.Equal(4000, trials);
    }

    private static byte[] Bytes(int count, int seed)
    {
        var bytes = new byte[count];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private static int IndexOf(byte[] haystack, byte[] needle) => haystack.AsSpan().IndexOf(needle);

    private sealed class ForwardOnly(byte[] bytes) : Stream
    {
        private readonly MemoryStream inner = new(bytes);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

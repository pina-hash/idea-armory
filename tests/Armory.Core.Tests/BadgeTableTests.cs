using System.Buffers.Binary;
using System.Diagnostics;
using Armory.Core;

namespace Armory.Core.Tests;

public sealed class BadgeTableTests
{
    private const string Root = @"C:\IDEA\Armory";
    private const long Generation = 0x0001_0000_0000_002A;
    // Core never calls Win32; Armory.Platform.Windows passes LCMapStringEx (ShellFold).
    private static readonly Func<char, char> Fold = char.ToUpperInvariant;

    private static readonly BadgeEntry[] Sample =
    [
        new("Robot 2027/Drivetrain/Plate.SLDPRT", BadgeState.Mine),
        new("Robot 2027/Drivetrain", BadgeState.Mine),
        new("Robot 2027", BadgeState.Attention),
        new("Robot 2027/Arm/Élan Bracket.SLDPRT", BadgeState.Locked),
        new("Robot 2027/Arm", BadgeState.Attention),
        new("Robot 2027/Arm/Broken.SLDASM", BadgeState.Attention),
        new("Robot 2027/Arm/Gear.SLDPRT", BadgeState.Synced),
        new("Class 2026/ñandú.sldprt", BadgeState.Mine),
    ];

    private static BadgeState Lookup(byte[] table, string path) => BadgeTable.Lookup(table, Generation, path, Fold);

    [Fact]
    public void The_header_fields_and_offsets_follow_format_1()
    {
        var table = BadgeTable.Build(Root, Sample, Generation, Fold);
        var span = table.AsSpan();
        Assert.Equal(0x54425241u, BinaryPrimitives.ReadUInt32LittleEndian(span));
        Assert.Equal("ARBT", System.Text.Encoding.ASCII.GetString(table, 0, 4));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(span[4..]));
        Assert.Equal(Generation, BinaryPrimitives.ReadInt64LittleEndian(span[8..]));
        Assert.Equal((uint)Sample.Length, BinaryPrimitives.ReadUInt32LittleEndian(span[16..]));
        var slots = BinaryPrimitives.ReadUInt32LittleEndian(span[20..]);
        Assert.Equal(16u, slots);
        var rootOffset = BinaryPrimitives.ReadUInt32LittleEndian(span[24..]);
        Assert.Equal(64u + slots * 16, rootOffset);
        Assert.Equal((uint)Root.Length, BinaryPrimitives.ReadUInt32LittleEndian(span[28..]));
        Assert.Equal(@"C:\IDEA\ARMORY", System.Text.Encoding.Unicode.GetString(table, (int)rootOffset, Root.Length * 2));
        var stringsOffset = BinaryPrimitives.ReadUInt32LittleEndian(span[32..]);
        Assert.Equal(rootOffset + Root.Length * 2, stringsOffset);
        var stringsUnits = BinaryPrimitives.ReadUInt32LittleEndian(span[36..]);
        Assert.Equal((uint)Sample.Sum(e => e.Path.Length), stringsUnits);
        Assert.Equal((uint)table.Length, BinaryPrimitives.ReadUInt32LittleEndian(span[40..]));
        Assert.Equal(stringsOffset + stringsUnits * 2, (uint)table.Length);
        Assert.All(table[44..64], b => Assert.Equal(0, b));
        // Every used slot points at its folded path in the pool and carries its state.
        var pool = System.Text.Encoding.Unicode.GetString(table, (int)stringsOffset, (int)stringsUnits * 2);
        var used = 0;
        for (var i = 0; i < slots; i++)
        {
            var slot = span.Slice(64 + i * 16, 16);
            var units = BinaryPrimitives.ReadUInt16LittleEndian(slot[12..]);
            if (units == 0) { Assert.Equal(0ul, BinaryPrimitives.ReadUInt64LittleEndian(slot)); continue; }
            used++;
            var key = pool.Substring((int)BinaryPrimitives.ReadUInt32LittleEndian(slot[8..]), units);
            Assert.Equal(BadgeTable.Hash(key), BinaryPrimitives.ReadUInt64LittleEndian(slot));
            var entry = Assert.Single(Sample, e => BadgeTable.Fold(e.Path.Replace('/', '\\'), Fold) == key);
            Assert.Equal((byte)entry.State, slot[14]);
            Assert.Equal(0, slot[15]);
        }
        Assert.Equal(Sample.Length, used);
        Assert.True(BadgeTable.IsValid(table, Generation));
        Assert.False(BadgeTable.IsValid(table, Generation + 1));
    }

    [Fact]
    public void The_hash_is_fnv1a_64_over_utf16_units()
    {
        Assert.Equal(14695981039346656037ul, BadgeTable.Hash(""));
        // One step per UTF-16 unit, not per byte: "A" is 0x41 then a multiply.
        Assert.Equal(unchecked((14695981039346656037ul ^ 0x41) * 1099511628211ul), BadgeTable.Hash("A"));
        Assert.Equal(unchecked((14695981039346656037ul ^ 0xC9) * 1099511628211ul), BadgeTable.Hash("É"));
    }

    [Theory]
    [InlineData(1, 16)]
    [InlineData(8, 16)]
    [InlineData(9, 32)]
    [InlineData(1000, 2048)]
    [InlineData(5000, 16384)]
    public void Slot_count_is_a_power_of_two_with_load_at_most_one_half(int count, int slots)
    {
        var entries = Enumerable.Range(0, count).Select(i => new BadgeEntry($"P/{i}.SLDPRT", BadgeState.Synced));
        var table = BadgeTable.Build(Root, entries, Generation, Fold);
        Assert.Equal((uint)slots, BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(20)));
        Assert.True(count * 2 <= slots);
    }

    [Fact]
    public void The_same_entries_in_any_order_give_the_same_bytes()
    {
        var one = BadgeTable.Build(Root, Sample, Generation, Fold);
        var two = BadgeTable.Build(Root, Sample.Reverse(), Generation, Fold);
        Assert.Equal(one, two);
        Assert.Equal(one, BadgeTable.Build(Root + @"\", Sample, Generation, Fold));
    }

    [Fact]
    public void Lookup_finds_every_entry_in_any_case_and_with_a_trailing_backslash()
    {
        var table = BadgeTable.Build(Root, Sample, Generation, Fold);
        foreach (var (path, state) in Sample)
        {
            var full = Root + @"\" + path.Replace('/', '\\');
            Assert.Equal(state, Lookup(table, full));
            Assert.Equal(state, Lookup(table, full.ToLowerInvariant()));
            Assert.Equal(state, Lookup(table, full.ToUpperInvariant()));
            Assert.Equal(state, Lookup(table, full + @"\"));
        }
        Assert.Equal(BadgeState.Locked, Lookup(table, @"c:\idea\armory\robot 2027\arm\élan bracket.SLDPRT"));
        Assert.Equal(BadgeState.Mine, Lookup(table, @"C:\IDEA\Armory\CLASS 2026\ÑANDÚ.SLDPRT"));
        Assert.Equal(BadgeState.None, Lookup(table, @"C:\IDEA\Armory\Robot 2027\Drivetrain\Other.SLDPRT"));
    }

    [Theory]
    [InlineData(@"C:\IDEA\Armory")]
    [InlineData(@"C:\IDEA\Armory\")]
    [InlineData(@"C:\IDEA\Armory\\")]
    [InlineData(@"C:\IDEA\ArmoryX\Robot 2027")]
    [InlineData(@"C:\IDEA\Armor")]
    [InlineData(@"C:\IDEA")]
    [InlineData(@"D:\IDEA\Armory\Robot 2027")]
    [InlineData(@"C:\Users\student\Documents\Plate.SLDPRT")]
    [InlineData(@"C:/IDEA/Armory/Robot 2027")]
    [InlineData(@"\\?\C:\IDEA\Armory\Robot 2027")]
    [InlineData("")]
    public void Lookup_rejects_the_root_itself_prefix_traps_and_other_places(string path)
    {
        var table = BadgeTable.Build(Root, Sample, Generation, Fold);
        Assert.Equal(BadgeState.None, Lookup(table, path));
    }

    [Fact]
    public void A_path_ends_at_its_first_nul_as_in_the_native_reader()
    {
        var table = BadgeTable.Build(Root, Sample, Generation, Fold);
        Assert.Equal(BadgeState.Attention, Lookup(table, "C:\\IDEA\\Armory\\Robot 2027\0\\Arm"));
    }

    [Fact]
    public void Paths_longer_than_1024_units_are_left_out_and_never_found()
    {
        var at = new string('a', 1020) + ".prt";      // 1024 units: kept
        var over = new string('b', 1021) + ".prt";    // 1025 units: left out
        var table = BadgeTable.Build(Root, [new(at, BadgeState.Mine), new(over, BadgeState.Mine)], Generation, Fold);
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(16)));
        Assert.Equal(BadgeState.Mine, Lookup(table, Root + @"\" + at));
        Assert.Equal(BadgeState.None, Lookup(table, Root + @"\" + over));
        // One unit over the limit, even as a trailing backslash, is refused like the DLL does.
        Assert.Equal(BadgeState.None, Lookup(table, Root + @"\" + at + @"\"));
    }

    [Fact]
    public void Forced_hash_collisions_probe_to_the_right_entry()
    {
        BadgeTable.HashFunction same = _ => 7;
        var entries = Enumerable.Range(0, 8).Select(i => new BadgeEntry($"Robot 2027/Part {i}.SLDPRT", (BadgeState)(1 + i % 4))).ToArray();
        var table = BadgeTable.Build(Root, entries, Generation, Fold, same);
        foreach (var (path, state) in entries)
            Assert.Equal(state, BadgeTable.Lookup(table, Generation, Root + @"\" + path.Replace('/', '\\'), Fold, same));
        Assert.Equal(BadgeState.None, BadgeTable.Lookup(table, Generation, Root + @"\Robot 2027\Part 9.SLDPRT", Fold, same));
        // The real hash on a colliding table finds nothing, and never loops.
        Assert.Equal(BadgeState.None, Lookup(table, Root + @"\Robot 2027\Part 1.SLDPRT"));
    }

    [Fact]
    public void A_full_ring_of_slots_with_no_match_ends_after_one_lap()
    {
        // 8 entries in 16 slots all hashing to 7 leave empty slots; a hand-made table with no
        // empty slot must still end. Fill every slot by hand and look for a missing key.
        BadgeTable.HashFunction same = _ => 3;
        var entries = Enumerable.Range(0, 8).Select(i => new BadgeEntry($"F{i:D2}", BadgeState.Mine)).ToArray();
        var table = BadgeTable.Build(Root, entries, Generation, Fold, same);
        for (var i = 0; i < 16; i++)
        {
            var slot = table.AsSpan(64 + i * 16);
            if (BinaryPrimitives.ReadUInt16LittleEndian(slot[12..]) == 0)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(slot, 99);
                BinaryPrimitives.WriteUInt16LittleEndian(slot[12..], 3);
            }
        }
        Assert.Equal(BadgeState.None, BadgeTable.Lookup(table, Generation, Root + @"\NOPE", Fold, same));
    }

    [Fact]
    public void Non_ascii_letters_fold_with_the_given_function_and_ascii_folds_inline()
    {
        var calls = new List<char>();
        char Counting(char c) { calls.Add(c); return char.ToUpperInvariant(c); }
        var table = BadgeTable.Build(Root, [new("Ñandú/Élan.prt", BadgeState.Mine)], Generation, Counting);
        Assert.Equal(new[] { 'Ñ', 'ú', 'É' }.Order(), calls.Distinct().Order());
        Assert.Equal(BadgeState.Mine, BadgeTable.Lookup(table, Generation, @"C:\IDEA\ARMORY\ÑANDÚ\ÉLAN.PRT", Counting));
        // A fold that leaves non-ASCII alone still folds ASCII: the key is "ñANDú\éLAN.PRT".
        var plain = BadgeTable.Build(Root, [new("ñandú/élan.prt", BadgeState.Mine)], Generation, c => c);
        Assert.Equal(BadgeState.Mine, BadgeTable.Lookup(plain, Generation, @"C:\idea\armory\ñANDú\éLAN.PRT", c => c));
        Assert.Equal(BadgeState.None, BadgeTable.Lookup(plain, Generation, @"C:\idea\armory\ÑANDÚ\ÉLAN.PRT", c => c));
    }

    [Fact]
    public void An_empty_table_is_valid_and_answers_none()
    {
        var table = BadgeTable.Build(Root, [], Generation, Fold);
        Assert.Equal(64 + 16 * 16 + Root.Length * 2, table.Length);
        Assert.True(BadgeTable.IsValid(table, Generation));
        Assert.Equal(BadgeState.None, Lookup(table, Root + @"\Robot 2027"));
    }

    [Fact]
    public void Duplicate_keys_keep_the_strongest_state_and_none_is_left_out()
    {
        var table = BadgeTable.Build(Root, [
            new("Robot 2027/A.prt", BadgeState.Synced), new("ROBOT 2027\\a.PRT", BadgeState.Mine), new("robot 2027/a.prt", BadgeState.Locked),
            new("Robot 2027/B.prt", BadgeState.None)], Generation, Fold);
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(16)));
        Assert.Equal(BadgeState.Mine, Lookup(table, Root + @"\Robot 2027\A.prt"));
        Assert.Equal(BadgeState.None, Lookup(table, Root + @"\Robot 2027\B.prt"));
    }

    [Fact]
    public void Bad_input_is_refused_and_a_damaged_table_answers_none()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => BadgeTable.Build(Root, Sample, 0, Fold));
        Assert.Throws<ArgumentException>(() => BadgeTable.Build(@"\", Sample, Generation, Fold));
        Assert.Throws<ArgumentException>(() => BadgeTable.Build("C:\\" + new string('x', 260), Sample, Generation, Fold));
        Assert.Throws<ArgumentException>(() => BadgeTable.Build(Root, [new("a", (BadgeState)9)], Generation, Fold));
        var good = BadgeTable.Build(Root, Sample, Generation, Fold);
        var path = Root + @"\Robot 2027";
        Assert.Equal(BadgeState.None, BadgeTable.Lookup(good, 0, path, Fold));
        Assert.Equal(BadgeState.None, BadgeTable.Lookup(good.AsSpan(0, 63), Generation, path, Fold));
        foreach (var (offset, value) in new (int, uint)[] { (0, 1), (4, 2), (20, 24), (20, 8), (16, 9), (28, 0), (28, 260), (24, 1), (32, 0xFFFFFFF0), (36, 0x7FFFFFFF), (40, 0xFFFFFFFF) })
        {
            var bad = (byte[])good.Clone();
            BinaryPrimitives.WriteUInt32LittleEndian(bad.AsSpan(offset), value);
            Assert.False(BadgeTable.IsValid(bad, Generation), $"offset {offset} = {value}");
            Assert.Equal(BadgeState.None, BadgeTable.Lookup(bad, Generation, path, Fold));
        }
        // A slot that points past the pool answers none instead of reading outside it.
        var outside = (byte[])good.Clone();
        for (var i = 0; i < 16; i++)
            if (BinaryPrimitives.ReadUInt16LittleEndian(outside.AsSpan(64 + i * 16 + 12)) != 0)
                BinaryPrimitives.WriteUInt32LittleEndian(outside.AsSpan(64 + i * 16 + 8), 0x00FFFFFF);
        Assert.All(Sample, e => Assert.Equal(BadgeState.None, Lookup(outside, Root + @"\" + e.Path.Replace('/', '\\'))));
        // An unknown state byte answers none.
        var strange = (byte[])good.Clone();
        for (var i = 0; i < 16; i++) strange[64 + i * 16 + 14] = 5;
        Assert.All(Sample, e => Assert.Equal(BadgeState.None, Lookup(strange, Root + @"\" + e.Path.Replace('/', '\\'))));
    }

    [Fact]
    public void A_drive_root_vault_works()
    {
        var table = BadgeTable.Build(@"D:\", [new("Robot/A.prt", BadgeState.Locked)], Generation, Fold);
        Assert.Equal(BadgeState.Locked, BadgeTable.Lookup(table, Generation, @"d:\robot\a.prt", Fold));
        Assert.Equal(BadgeState.None, BadgeTable.Lookup(table, Generation, @"D:", Fold));
        Assert.Equal(BadgeState.None, BadgeTable.Lookup(table, Generation, @"D:\", Fold));
    }

    [Fact]
    public void The_header_carries_magic_version_generation_publisher_time_and_newest()
    {
        var header = new byte[BadgeTable.HeaderBytes];
        BadgeTable.WriteHeader(header, Generation, 4242, 133_000_000_000_000_000, newest: Generation + 5);
        Assert.Equal("ARBH", System.Text.Encoding.ASCII.GetString(header, 0, 4));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4)));
        Assert.Equal(Generation, BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(8)));
        Assert.Equal(4242u, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(16)));
        Assert.Equal(133_000_000_000_000_000, BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(24)));
        Assert.Equal(Generation + 5, BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(32)));
        Assert.All(header[40..], b => Assert.Equal(0, b));
        Assert.Equal(Generation, BadgeTable.HeaderGeneration(header));
        Assert.Equal(Generation + 5, BadgeTable.HeaderNewest(header));
        // Newest is never below the generation named now, and generation 0 keeps it.
        BadgeTable.WriteHeader(header, 0, 1, 1, newest: 77);
        Assert.Equal(0, BadgeTable.HeaderGeneration(header));
        Assert.Equal(77, BadgeTable.HeaderNewest(header));
        BadgeTable.WriteHeader(header, 90, 1, 1, newest: 77);
        Assert.Equal(90, BadgeTable.HeaderNewest(header));
        header[0] = 0;
        Assert.Equal(0, BadgeTable.HeaderGeneration(header));
        Assert.Equal(0, BadgeTable.HeaderNewest(header));
        Assert.Throws<ArgumentException>(() => BadgeTable.WriteHeader(new byte[32], 1, 1, 1));
    }

    [Fact]
    public void Section_names_end_in_the_generation_as_16_uppercase_hex_digits()
    {
        Assert.Equal(@"Local\IDEA-Armory-Badges-S-1-5-21-1-2-3-1001-0001000000002A2B",
            BadgeTable.SectionName(@"Local\IDEA-Armory-Badges-S-1-5-21-1-2-3-1001", 0x0001000000002A2B));
        Assert.EndsWith("-000000000000002A", BadgeTable.SectionName("x", 42));
    }

    // The owner wants every synced file in the table: size and time for 5,000 and 20,000 files
    // with paths as long as a real team's.
    [Theory]
    [InlineData(5_000, 1_000_000)]
    [InlineData(20_000, 4_000_000)]
    public void Every_synced_file_fits_a_table_of_a_few_megabytes(int files, int maxBytes)
    {
        var entries = Enumerable.Range(0, files)
            .Select(i => new BadgeEntry($"Robot 2027/Subsystem {i % 40:D2}/Assembly {i % 7}/Part {i:D5} rev B.SLDPRT", (BadgeState)(1 + i % 4)))
            .ToArray();
        var watch = Stopwatch.StartNew();
        var table = BadgeTable.Build(Root, BadgeRules.WithFolders(entries), Generation, Fold);
        watch.Stop();
        Assert.True(table.Length < maxBytes, $"{files} files: {table.Length:N0} bytes");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"{files} files: built in {watch.ElapsedMilliseconds} ms");
        foreach (var i in new[] { 0, 1, 2, 3, files / 2, files - 1 })
            Assert.Equal(entries[i].State, Lookup(table, Root + @"\" + entries[i].Path.Replace('/', '\\')));
    }
}

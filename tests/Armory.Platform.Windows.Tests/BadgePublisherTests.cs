using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using System.Runtime.Versioning;
using Armory.Core;

namespace Armory.Platform.Windows.Tests;

// A clock the test moves by hand.
internal sealed class FakeTime(DateTimeOffset start) : TimeProvider
{
    internal DateTimeOffset Now { get; set; } = start;
    public override DateTimeOffset GetUtcNow() => Now;
}

[SupportedOSPlatform("windows")]
public sealed class BadgePublisherTests
{
    private const string Root = @"C:\IDEA\Armory";
    private static readonly BadgeEntry[] Entries =
    [
        new("Robot 2027", BadgeState.Mine),
        new("Robot 2027/Plate.SLDPRT", BadgeState.Mine),
        new("Robot 2027/Gear.SLDPRT", BadgeState.Synced),
        new("Robot 2027/Élan.SLDPRT", BadgeState.Locked),
    ];

    // A private header name, so a test never touches a real Armory's badges.
    internal static string PrivateName() => @"Local\IDEA-Armory-Badges-Test-" + Guid.NewGuid().ToString("N");

    private static byte[] ReadSection(string name, int length)
    {
        using var map = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.Read);
        using var view = map.CreateViewAccessor(0, length, MemoryMappedFileAccess.Read);
        var bytes = new byte[length];
        view.ReadArray(0, bytes, 0, length);
        return bytes;
    }

    private static bool Exists(string name)
    {
        try
        {
            using var map = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.Read);
            return true;
        }
        catch (FileNotFoundException) { return false; }
    }

    [WindowsFact]
    public void Publish_writes_a_header_and_a_table_a_reader_can_open()
    {
        var name = PrivateName();
        var time = new FakeTime(new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero));
        using var publisher = new BadgePublisher(name, time);
        Assert.Equal(0, publisher.Generation);
        var publication = publisher.Publish(Root, Entries);
        Assert.Equal(publication.Generation, publisher.Generation);
        Assert.Equal(Entries.Length, publication.Entries);
        // The first generation of a run is the current time, so it never repeats an older run's.
        Assert.True(publication.Generation >= time.Now.ToFileTime());

        var header = ReadSection(name, BadgeTable.HeaderBytes);
        Assert.Equal(BadgeTable.HeaderMagic, BinaryPrimitives.ReadUInt32LittleEndian(header));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4)));
        Assert.Equal(publication.Generation, BadgeTable.HeaderGeneration(header));
        Assert.Equal((uint)Environment.ProcessId, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(16)));
        Assert.Equal(time.Now.ToFileTime(), BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(24)));

        var table = ReadSection(BadgeTable.SectionName(name, publication.Generation), publication.Bytes);
        Assert.True(BadgeTable.IsValid(table, publication.Generation));
        Assert.Equal(BadgeState.Locked, BadgeTable.Lookup(table, publication.Generation, @"c:\idea\armory\robot 2027\élan.sldprt", ShellFold.Fold));
        Assert.Equal(BadgeState.Mine, BadgeTable.Lookup(table, publication.Generation, @"C:\IDEA\Armory\Robot 2027\", ShellFold.Fold));
    }

    [WindowsFact]
    public void Republishing_counts_up_by_one_and_keeps_two_old_tables_for_five_seconds()
    {
        var name = PrivateName();
        var time = new FakeTime(DateTimeOffset.UtcNow);
        using var publisher = new BadgePublisher(name, time);
        var generations = Enumerable.Range(0, 4).Select(i => publisher.Publish(Root, Entries.Take(i + 1)).Generation).ToArray();
        Assert.Equal(Enumerable.Range(0, 4).Select(i => generations[0] + i), generations);
        Assert.Equal(2, publisher.KeptTables);
        Assert.False(Exists(BadgeTable.SectionName(name, generations[0])));
        Assert.True(Exists(BadgeTable.SectionName(name, generations[1])));
        Assert.True(Exists(BadgeTable.SectionName(name, generations[2])));
        Assert.True(Exists(BadgeTable.SectionName(name, generations[3])));
        time.Now += TimeSpan.FromSeconds(4);
        publisher.Sweep();
        Assert.Equal(2, publisher.KeptTables);
        time.Now += TimeSpan.FromSeconds(1);
        publisher.Sweep();
        Assert.Equal(0, publisher.KeptTables);
        Assert.False(Exists(BadgeTable.SectionName(name, generations[1])));
        Assert.False(Exists(BadgeTable.SectionName(name, generations[2])));
        Assert.True(Exists(BadgeTable.SectionName(name, generations[3])));
    }

    [WindowsFact]
    public void Clear_and_quit_set_generation_zero_at_once()
    {
        var name = PrivateName();
        using var reader = MemoryMappedFile.CreateOrOpen(name, BadgeTable.HeaderBytes);
        long first;
        using (var publisher = new BadgePublisher(name))
        {
            first = publisher.Publish(Root, Entries).Generation;
            publisher.Clear();
            Assert.Equal(0, publisher.Generation);
            Assert.Equal(0, BadgeTable.HeaderGeneration(ReadSection(name, BadgeTable.HeaderBytes)));
            var again = publisher.Publish(Root, Entries).Generation;
            Assert.Equal(first + 1, again);
            Assert.Equal(again, BadgeTable.HeaderGeneration(ReadSection(name, BadgeTable.HeaderBytes)));
        }
        Assert.Equal(0, BadgeTable.HeaderGeneration(ReadSection(name, BadgeTable.HeaderBytes)));
        Assert.False(Exists(BadgeTable.SectionName(name, first + 1)));
    }

    [WindowsFact]
    public void A_restart_over_a_living_header_takes_it_over_and_never_repeats_a_generation()
    {
        var name = PrivateName();
        // Explorer still maps the header of an Armory that ended; it named a generation far ahead.
        using var explorer = MemoryMappedFile.CreateOrOpen(name, BadgeTable.HeaderBytes);
        var ahead = DateTimeOffset.UtcNow.AddYears(5).ToFileTime();
        using (var view = explorer.CreateViewAccessor(0, BadgeTable.HeaderBytes))
        {
            var bytes = new byte[BadgeTable.HeaderBytes];
            BadgeTable.WriteHeader(bytes, ahead, 999_999, 0);
            view.WriteArray(0, bytes, 0, bytes.Length);
        }
        using var publisher = new BadgePublisher(name);
        var header = ReadSection(name, BadgeTable.HeaderBytes);
        Assert.Equal(0, BadgeTable.HeaderGeneration(header));
        Assert.Equal((uint)Environment.ProcessId, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(16)));
        Assert.Equal(ahead + 1, publisher.Publish(Root, Entries).Generation);
        publisher.Dispose();
        using var next = new BadgePublisher(name);
        Assert.Equal(ahead + 2, next.Publish(Root, Entries).Generation);
    }

    [WindowsFact]
    public void Twenty_thousand_synced_files_publish_in_well_under_a_second()
    {
        var entries = BadgeRules.WithFolders(Enumerable.Range(0, 20_000)
            .Select(i => new BadgeEntry($"Robot 2027/Subsystem {i % 40:D2}/Part {i:D5} rev B.SLDPRT", BadgeState.Synced)));
        using var publisher = new BadgePublisher(PrivateName());
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var publication = publisher.Publish(Root, entries);
        watch.Stop();
        Assert.Equal(20_000, publication.Entries);
        Assert.True(publication.Bytes < 4_000_000, publication.Bytes.ToString());
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), watch.ElapsedMilliseconds + " ms");
    }
}

public sealed class ShellFoldTests
{
    [Fact]
    public void Ascii_folds_inline_and_only_a_to_z_change()
    {
        Assert.Equal('A', ShellFold.Fold('a'));
        Assert.Equal('Z', ShellFold.Fold('z'));
        Assert.Equal('\\', ShellFold.Fold('\\'));
        Assert.Equal('1', ShellFold.Fold('1'));
        Assert.Equal('{', ShellFold.Fold('{'));
        Assert.Equal('`', ShellFold.Fold('`'));
    }

    [WindowsFact]
    public void Non_ascii_letters_fold_through_windows_invariant_uppercase_one_unit_at_a_time()
    {
        Assert.Equal('É', ShellFold.Fold('é'));
        Assert.Equal('Ñ', ShellFold.Fold('ñ'));
        Assert.Equal('Ú', ShellFold.Fold('ú'));
        Assert.Equal('Ω', ShellFold.Fold('ω'));
        Assert.Equal('É', ShellFold.Fold('É'));
        // No single-unit uppercase: unchanged, so lengths never change.
        Assert.Equal('ß', ShellFold.Fold('ß'));
        Assert.Equal('\uD801', ShellFold.Fold('\uD801'));
        // Asked twice, the same answer (the second from memory).
        Assert.Equal(ShellFold.Fold('ü'), ShellFold.Fold('ü'));
    }
}

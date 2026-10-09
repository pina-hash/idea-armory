using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Armory.Core;

namespace Armory.Platform.Windows;

// What one Publish put out: its generation, how many paths have a badge, and the table's size.
public sealed record BadgePublication(long Generation, int Entries, int Bytes);

// Publishes Armory's badges for ArmoryBadges.dll inside File Explorer (docs/agent/EXPLORER.md,
// format 1): a 4096-byte header section that names the current generation, and one immutable
// table section per generation, both pagefile-backed, named for this Windows user in this
// session (Local\), so nothing is written to disk and nothing outlives Armory.
//
// Publish builds a whole new table, then switches the header's generation with one aligned
// 64-bit store. The two previous tables stay open for 5 seconds, for a handler that read the
// old generation a moment ago (a handler that mapped a table keeps it alive by itself). Clear
// and Dispose set generation 0, which every handler answers with no badge at once. A new
// publisher over a header that is still alive (Explorer keeps it mapped after Armory quit or
// crashed) takes it over: its own process id, no badges until its first Publish, and a
// generation above the newest that header ever named (kept in the header for this).
[SupportedOSPlatform("windows")]
public sealed class BadgePublisher : IDisposable
{
    public const string HeaderPrefix = @"Local\IDEA-Armory-Badges-";
    public static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(5);
    public const int KeptTablesAtMost = 2;

    private readonly Lock gate = new();
    private readonly TimeProvider time;
    private readonly MemoryMappedFile header;
    private readonly MemoryMappedViewAccessor headerView;
    private readonly List<(MemoryMappedFile Table, DateTimeOffset RetiredAt)> retired = [];
    private readonly ITimer sweeper;
    private MemoryMappedFile? current;
    private long lastGeneration;
    private bool published;
    private bool startedThisRun;
    private bool disposed;

    // Local\IDEA-Armory-Badges-<SID><instance suffix>. A test instance of Armory (ARMORY_DATA_DIR)
    // passes AgentPaths.InstanceSuffix, so it never replaces a real Armory's badges; the DLL
    // reads that name only when ARMORY_BADGES_SECTION names it.
    public static string HeaderNameFor(string userSid, string instanceSuffix = "") => HeaderPrefix + userSid + instanceSuffix;

    public static string DefaultHeaderName(string instanceSuffix = "") =>
        HeaderNameFor(WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("This Windows account has no SID."), instanceSuffix);

    public BadgePublisher(string? headerName = null, TimeProvider? timeProvider = null)
    {
        HeaderName = headerName ?? DefaultHeaderName();
        time = timeProvider ?? TimeProvider.System;
        header = MemoryMappedFile.CreateOrOpen(HeaderName, BadgeTable.HeaderBytes, MemoryMappedFileAccess.ReadWrite);
        try
        {
            headerView = header.CreateViewAccessor(0, BadgeTable.HeaderBytes, MemoryMappedFileAccess.ReadWrite);
            // A header left by an earlier Armory: never reuse a generation it published.
            var existing = new byte[BadgeTable.HeaderBytes];
            headerView.ReadArray(0, existing, 0, existing.Length);
            lastGeneration = Math.Max(0, BadgeTable.HeaderNewest(existing));
            // Take it over with no badges until the first Publish.
            StoreGeneration(0);
            headerView.Write(0, BadgeTable.HeaderMagic);
            headerView.Write(4, BadgeTable.FormatVersion);
            headerView.Write(BadgeTable.HeaderPidOffset, (uint)Environment.ProcessId);
            headerView.Write(20, 0u);
            headerView.Write(BadgeTable.HeaderUpdatedAtOffset, time.GetUtcNow().ToFileTime());
            headerView.Write(BadgeTable.HeaderNewestOffset, lastGeneration);
        }
        catch
        {
            headerView?.Dispose();
            header.Dispose();
            throw;
        }
        sweeper = time.CreateTimer(_ => Sweep(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public string HeaderName { get; }

    // The generation the header names now; 0 before the first Publish and after Clear.
    public long Generation
    {
        get { lock (gate) return published ? lastGeneration : 0; }
    }

    // Old tables still open (kept for a handler that read their generation a moment ago).
    public int KeptTables
    {
        get { lock (gate) return retired.Count; }
    }

    // Builds the table for these entries (Armory.Core.BadgeRules.Entries) and publishes it.
    public BadgePublication Publish(string vaultRoot, IEnumerable<BadgeEntry> entries)
    {
        var list = entries as IReadOnlyCollection<BadgeEntry> ?? entries.ToArray();
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            // The first table of this run starts at the current time, so a restarted Armory never
            // names a section an Explorer still holds from the last run.
            var generation = startedThisRun ? lastGeneration + 1 : Math.Max(lastGeneration + 1, time.GetUtcNow().ToFileTime());
            byte[] bytes;
            MemoryMappedFile table;
            for (var attempt = 0; ; attempt++)
            {
                bytes = BadgeTable.Build(vaultRoot, list, generation, ShellFold.Fold);
                try
                {
                    table = MemoryMappedFile.CreateNew(BadgeTable.SectionName(HeaderName, generation), bytes.Length, MemoryMappedFileAccess.ReadWrite);
                    break;
                }
                catch (IOException) when (attempt < 8)
                {
                    // That name is still held by someone: the next generation.
                    generation++;
                }
            }
            using (var view = table.CreateViewAccessor(0, bytes.Length, MemoryMappedFileAccess.ReadWrite))
                view.WriteArray(0, bytes, 0, bytes.Length);
            headerView.Write(BadgeTable.HeaderPidOffset, (uint)Environment.ProcessId);
            headerView.Write(BadgeTable.HeaderUpdatedAtOffset, time.GetUtcNow().ToFileTime());
            headerView.Write(BadgeTable.HeaderNewestOffset, generation);
            StoreGeneration(generation);
            Retire(current);
            current = table;
            lastGeneration = generation;
            published = true;
            startedThisRun = true;
            return new BadgePublication(generation, BitConverter.ToInt32(bytes, 16), bytes.Length);
        }
    }

    // Every badge off at once (generation 0); Publish turns them back on.
    public void Clear()
    {
        lock (gate)
        {
            if (disposed) return;
            StoreGeneration(0);
            published = false;
            Retire(current);
            current = null;
        }
    }

    // Closes old tables whose 5 seconds are over. Called by its own timer, and by tests.
    internal void Sweep()
    {
        lock (gate)
        {
            var now = time.GetUtcNow();
            for (var i = retired.Count - 1; i >= 0; i--)
            {
                if (now - retired[i].RetiredAt < KeepAlive) continue;
                retired[i].Table.Dispose();
                retired.RemoveAt(i);
            }
        }
    }

    // On quit: generation 0, then every section closed (a handler that mapped one keeps its own).
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            StoreGeneration(0);
            current?.Dispose();
            current = null;
            foreach (var (table, _) in retired) table.Dispose();
            retired.Clear();
            headerView.Dispose();
            header.Dispose();
        }
        sweeper.Dispose();
    }

    private void Retire(MemoryMappedFile? table)
    {
        if (table is null) return;
        retired.Add((table, time.GetUtcNow()));
        while (retired.Count > KeptTablesAtMost)
        {
            retired[0].Table.Dispose();
            retired.RemoveAt(0);
        }
        sweeper.Change(KeepAlive, Timeout.InfiniteTimeSpan);
    }

    // One aligned 64-bit store, with full fences: every byte of the table (written through its
    // own view, in this process) is visible before a handler can see the new generation.
    private void StoreGeneration(long generation)
    {
        var handle = headerView.SafeMemoryMappedViewHandle;
        var added = false;
        handle.DangerousAddRef(ref added);
        try
        {
            var address = handle.DangerousGetHandle() + (nint)headerView.PointerOffset;
            Interlocked.MemoryBarrier();
            Marshal.WriteInt64(address, BadgeTable.HeaderGenerationOffset, generation);
            Interlocked.MemoryBarrier();
        }
        finally
        {
            if (added) handle.DangerousRelease();
        }
    }
}

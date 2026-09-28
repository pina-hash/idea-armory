using Armory.Core;

namespace Armory.Core.Tests;

internal static class Fixtures
{
    internal static VaultPath Path(string value = "robot/plate.txt")
    {
        Assert.True(VaultPath.TryCreate(value, out var path, out var error), error);
        return path;
    }
    internal static Revision Base => new("v1", "base", "Alex");
    internal static Revision Newer => new("v2", "remote", "Maria");
    internal static SyncInput Input => new(Path(), Base, "base", Base, LockOwnership.Free, false, true);
}

internal sealed class SimulatedCrash : Exception;
internal sealed class MemoryJournalStore : IJournalStore
{
    internal List<byte> Bytes { get; } = [];
    internal int? CrashAfter { get; set; }
    internal bool CrashOnFlush { get; set; }
    public byte[] ReadAll() => Bytes.ToArray();
    public void Append(ReadOnlySpan<byte> bytes)
    {
        var count = Math.Min(CrashAfter ?? bytes.Length, bytes.Length);
        Bytes.AddRange(bytes[..count].ToArray());
        if (CrashAfter is not null) { CrashAfter = null; throw new SimulatedCrash(); }
    }
    public void Flush()
    {
        if (CrashOnFlush) { CrashOnFlush = false; throw new SimulatedCrash(); }
    }
    public void TruncateIncompleteTail(int validLength) => Bytes.RemoveRange(validLength, Bytes.Count - validLength);
}

internal sealed class RecordingSink : IIntentSink
{
    internal List<JournalEntry> Applied { get; } = [];
    internal bool CrashAfterCommit { get; set; }
    public void ApplyOnce(JournalEntry entry)
    {
        var existing = Applied.FirstOrDefault(e => e.Id == entry.Id);
        if (existing is not null) { Assert.Equal(existing, entry); return; }
        Applied.Add(entry);
        if (CrashAfterCommit) { CrashAfterCommit = false; throw new SimulatedCrash(); }
    }
}

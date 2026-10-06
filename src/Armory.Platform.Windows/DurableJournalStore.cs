using System.Buffers.Binary;
using System.Text.Json;
using Armory.Core;

namespace Armory.Platform.Windows;

public sealed record JournalRecovery(long DroppedBytes, bool CorruptionDetected, long FailureOffset, string? EvidencePath);

// Outer CRC-32C transport frames preserve C1's existing SHA-256/commit-marker encoding.
public sealed class DurableJournalStore : IJournalStore, IDisposable
{
    private const int HeaderSize = 16;
    private const int MaximumRecord = 2 * 1024 * 1024;
    private const uint Magic = 0x324A5241;
    private readonly FileStream stream;
    private readonly List<(long PhysicalEnd, int LogicalEnd)> boundaries = [];
    private readonly MemoryStream logical = new();
    private readonly object gate = new();
    private bool faulted;
    private long generation;
    public JournalRecovery Recovery { get; }
    public DurableJournalStore(string file)
    {
        var absolute = Path.GetFullPath(file);
        WindowsPaths.RejectReparsePoints(absolute);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        stream = new(WindowsPaths.Extended(absolute), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read,
            4096, FileOptions.WriteThrough);
        try
        {
            Recovery = File.Exists(absolute + ".blocked")
                ? JsonSerializer.Deserialize<JournalRecovery>(File.ReadAllText(absolute + ".blocked")) ?? throw new InvalidDataException("Invalid corruption marker.")
                : Recover(absolute);
        }
        catch { stream.Dispose(); logical.Dispose(); throw; }
    }
    private JournalRecovery Recover(string file)
    {
        var header = new byte[HeaderSize];
        long valid = 0;
        var corrupt = false;
        while (stream.Position < stream.Length)
        {
            var remaining = stream.Length - stream.Position;
            if (remaining < HeaderSize) break;
            stream.ReadExactly(header);
            var size = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != Magic || size is < 1 or > MaximumRecord ||
                BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8)) != ~size) { corrupt = true; break; }
            if (stream.Length - stream.Position < size) break;
            var payload = new byte[size];
            stream.ReadExactly(payload);
            if (Crc32C.Compute(payload) != BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12))) { corrupt = true; break; }
            logical.Write(payload);
            valid = stream.Position;
            boundaries.Add((valid, checked((int)logical.Length)));
        }
        var dropped = stream.Length - valid;
        string? evidence = null;
        if (dropped > 0)
        {
            // A checksum failure may be committed-data corruption, not a torn write.
            // Preserve the full original before truncating; block reads/appends for a lead.
            if (corrupt)
            {
                evidence = file + ".corrupt-" + Guid.NewGuid().ToString("N");
                stream.Position = 0;
                using var backup = new FileStream(WindowsPaths.Extended(evidence), FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
                stream.CopyTo(backup);
                backup.Flush(true);
                using var marker = new FileStream(WindowsPaths.Extended(file + ".blocked"), FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
                JsonSerializer.Serialize(marker, new JournalRecovery(dropped, true, valid, evidence));
                marker.Flush(true);
            }
            stream.SetLength(valid);
            stream.Flush(true);
        }
        stream.Position = valid;
        return new(dropped, corrupt, valid, evidence);
    }
    private void CheckHealthy()
    {
        ObjectDisposedException.ThrowIf(!stream.CanWrite, this);
        if (faulted) throw new IOException("A journal write failed; close and reopen the store before retrying.");
        if (Recovery.CorruptionDetected) throw new InvalidDataException($"Journal corruption at {Recovery.FailureOffset}; evidence retained at {Recovery.EvidencePath}.");
    }
    public byte[] ReadAll() { lock (gate) { CheckHealthy(); return logical.ToArray(); } }
    // The logical bytes change only through Append and TruncateIncompleteTail, which bump this.
    public long? Generation { get { lock (gate) { CheckHealthy(); return generation; } } }
    public void Append(ReadOnlySpan<byte> bytes)
    {
        lock (gate)
        {
            CheckHealthy();
            if (bytes.Length is < 1 or > MaximumRecord) throw new ArgumentOutOfRangeException(nameof(bytes));
            Span<byte> header = stackalloc byte[HeaderSize];
            BinaryPrimitives.WriteUInt32LittleEndian(header, Magic);
            BinaryPrimitives.WriteInt32LittleEndian(header[4..], bytes.Length);
            BinaryPrimitives.WriteInt32LittleEndian(header[8..], ~bytes.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(header[12..], Crc32C.Compute(bytes));
            // Separate writes let a killed process leave a recoverable incomplete frame.
            try
            {
                stream.Write(header);
                stream.Write(bytes);
                stream.Flush(true);
                logical.Write(bytes);
                generation++;
                boundaries.Add((stream.Position, checked((int)logical.Length)));
            }
            catch { faulted = true; throw; }
        }
    }
    public void Flush() { lock (gate) { CheckHealthy(); stream.Flush(true); } }
    public void TruncateIncompleteTail(int validLength)
    {
        lock (gate)
        {
            CheckHealthy();
            var coreFrames = new OfflineJournal(this).Read();
            if (!coreFrames.TornTail || coreFrames.ValidLength != validLength)
                throw new InvalidDataException("Only the core journal's verified incomplete suffix may be truncated.");
            var index = boundaries.FindIndex(b => b.LogicalEnd == validLength);
            if (validLength != 0 && index < 0) throw new InvalidDataException("Truncation must land on a complete transport-record boundary.");
            var end = validLength == 0 ? 0 : boundaries[index].PhysicalEnd;
            stream.SetLength(end);
            stream.Position = end;
            stream.Flush(true);
            logical.SetLength(validLength);
            logical.Position = validLength;
            generation++;
            boundaries.RemoveAll(b => b.LogicalEnd > validLength);
        }
    }
    public void Dispose() { lock (gate) { stream.Dispose(); logical.Dispose(); } }
}

internal static class Crc32C
{
    private static readonly uint[] Table = Enumerable.Range(0, 256).Select(i =>
    {
        var crc = (uint)i;
        for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0x82f63b78u : 0);
        return crc;
    }).ToArray();
    internal static uint Compute(ReadOnlySpan<byte> bytes)
    {
        var crc = uint.MaxValue;
        foreach (var value in bytes) crc = Table[(crc ^ value) & 255] ^ (crc >> 8);
        return ~crc;
    }
}

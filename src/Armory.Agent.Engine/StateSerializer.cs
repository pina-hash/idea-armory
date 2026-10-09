using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Armory.Agent.Engine;

// Writes the state document (EngineState) as pieces that are written one after the other, and
// serializes again only what changed since the last time. File records are kept in blocks of
// 128: a block's bytes are built again only when one of its records changed (FileState.Changed),
// came or went (FileTable), and each record's own JSON only when that record changed. The
// completed ids, which only grow, are written once each, and each import summary and each release
// stamp (both immutable) once.
// The rest is small and serialized every time. The result is the document the reflection
// serializer writes (EngineState.SerializeWhole), with its file records in another order; a unit
// test holds the two to each other. With 5,000 files the document is megabytes, and the engine
// saves it before every server write (group commit): serializing all of it every time would cost
// more than the transfers.
internal sealed class StateSerializer
{
    private const int BlockSize = 128;
    private static readonly byte[] Open = "{"u8.ToArray(), Close = "}"u8.ToArray(), Comma = ","u8.ToArray(),
        OpenArray = "["u8.ToArray(), CloseArray = "]"u8.ToArray();
    private readonly JsonTypeInfo document = EngineState.Options.GetTypeInfo(typeof(EngineState));
    private readonly Dictionary<string, byte[]> names = new(StringComparer.Ordinal);
    private readonly List<Block> blocks = [];
    private readonly List<byte[]> small = [], smallBefore = [];
    private Dictionary<ImportRecord, byte[]> imports = new(ReferenceEqualityComparer.Instance), importsSpare = new(ReferenceEqualityComparer.Instance);
    private Dictionary<StampRecord, (string Hash, byte[] Bytes)> stamps = new(ReferenceEqualityComparer.Instance), stampsSpare = new(ReferenceEqualityComparer.Instance);
    private byte[] completed = new byte[1024];
    private int completedLength, completedCount = -1, completedRemovals;
    private bool filesMoved = true, changed;

    // Records written together; Bytes is null until built again after a change.
    internal sealed class Block
    {
        internal readonly List<(string Key, FileState State)> Records = new(BlockSize);
        internal byte[]? Bytes;
    }

    internal StateSerializer(EngineState state)
    {
        foreach (var (key, st) in state.Files) Added(key, st);
    }

    internal void Added(string key, FileState st)
    {
        if (st.Block is not null) Removed(st);
        if (blocks.Count == 0 || blocks[^1].Records.Count >= BlockSize) blocks.Add(new Block());
        var block = blocks[^1];
        block.Records.Add((key, st));
        block.Bytes = null;
        st.Block = block;
        filesMoved = true;
    }

    internal void Removed(FileState st)
    {
        if (st.Block is not { } block) return;
        st.Block = null;
        var at = block.Records.FindIndex(r => ReferenceEquals(r.State, st));
        if (at >= 0) block.Records.RemoveAt(at);
        block.Bytes = null;
        if (block.Records.Count == 0) blocks.Remove(block);
        filesMoved = true;
    }

    internal void Changed(FileState st)
    {
        if (st.Block is { } block) block.Bytes = null;
    }

    // The document's pieces, or null when nothing changed since the last call (unless whole).
    internal IReadOnlyList<ReadOnlyMemory<byte>>? Serialize(EngineState state, bool whole)
    {
        List<ReadOnlyMemory<byte>> parts = [Open];
        small.Clear();
        changed = false;
        var first = true;
        foreach (var property in document.Properties)
        {
            if (property.Get is null) continue;
            var special = property.Name is "files" or "completed" or "imports" or "releaseStamps";
            var value = special ? null : property.Get(state);
            if (value is null && !special) continue; // JsonIgnoreCondition.WhenWritingNull
            if (!first) parts.Add(Comma);
            first = false;
            parts.Add(NameOf(property.Name));
            switch (property.Name)
            {
                case "files": AddFiles(parts); break;
                case "completed": AddCompleted(parts, state.Completed); break;
                case "imports": AddImports(parts, state.Imports); break;
                case "releaseStamps": AddStamps(parts, state.ReleaseStamps); break;
                default:
                    var bytes = JsonSerializer.SerializeToUtf8Bytes(value, property.PropertyType, EngineState.Options);
                    small.Add(bytes);
                    parts.Add(bytes);
                    break;
            }
        }
        parts.Add(Close);
        if (small.Count != smallBefore.Count) changed = true;
        else for (var i = 0; i < small.Count && !changed; i++) changed = !small[i].AsSpan().SequenceEqual(smallBefore[i]);
        smallBefore.Clear();
        smallBefore.AddRange(small);
        return changed || whole ? parts : null;
    }

    internal static byte[] Join(IReadOnlyList<ReadOnlyMemory<byte>> parts)
    {
        var length = 0;
        foreach (var part in parts) length += part.Length;
        var bytes = new byte[length];
        var at = 0;
        foreach (var part in parts)
        {
            part.Span.CopyTo(bytes.AsSpan(at));
            at += part.Length;
        }
        return bytes;
    }

    // "name": as the serializer writes a property name.
    private byte[] NameOf(string name)
    {
        if (names.TryGetValue(name, out var bytes)) return bytes;
        var encoded = JsonSerializer.SerializeToUtf8Bytes(name, EngineState.Options);
        bytes = new byte[encoded.Length + 1];
        encoded.CopyTo(bytes, 0);
        bytes[^1] = (byte)':';
        return names[name] = bytes;
    }

    private void AddFiles(List<ReadOnlyMemory<byte>> parts)
    {
        parts.Add(Open);
        var first = true;
        foreach (var block in blocks)
        {
            if (block.Bytes is null)
            {
                block.Bytes = Build(block);
                changed = true;
            }
            if (!first) parts.Add(Comma);
            first = false;
            parts.Add(block.Bytes);
        }
        parts.Add(Close);
        if (filesMoved) changed = true;
        filesMoved = false;
    }

    // "key":{...},"key":{...} for every record of the block, each serialized again only if it changed.
    private static byte[] Build(Block block)
    {
        var length = Math.Max(0, block.Records.Count - 1);
        foreach (var (key, st) in block.Records)
        {
            if (st.Json is null || st.JsonVersion != st.Version || !string.Equals(st.JsonKey, key, StringComparison.Ordinal))
            {
                st.Json = Entry(key, st);
                st.JsonKey = key;
                st.JsonVersion = st.Version;
            }
            length += st.Json.Length;
        }
        var bytes = new byte[length];
        var at = 0;
        foreach (var (_, st) in block.Records)
        {
            if (at > 0) bytes[at++] = (byte)',';
            st.Json!.CopyTo(bytes, at);
            at += st.Json!.Length;
        }
        return bytes;
    }

    private static byte[] Entry(string key, FileState st)
    {
        var name = JsonSerializer.SerializeToUtf8Bytes(key, EngineState.Options);
        var value = JsonSerializer.SerializeToUtf8Bytes(st, EngineState.Options);
        var entry = new byte[name.Length + 1 + value.Length];
        name.CopyTo(entry, 0);
        entry[name.Length] = (byte)':';
        value.CopyTo(entry, name.Length + 1);
        return entry;
    }

    // The ids so far, '[' and every id with its comma, kept and grown (a part already handed out
    // is never written over); ']' is added when written.
    private void AddCompleted(List<ReadOnlyMemory<byte>> parts, IdSet ids)
    {
        if (completedCount < 0 || ids.Removals != completedRemovals || ids.Count < completedCount)
        {
            completed = new byte[Math.Max(1024, completed.Length)];
            completed[0] = (byte)'[';
            completedLength = 1;
            completedCount = 0;
            completedRemovals = ids.Removals;
            changed = true;
        }
        var list = ids.InOrder;
        if (list.Count != completedCount) changed = true;
        for (var i = completedCount; i < list.Count; i++)
        {
            var item = JsonSerializer.SerializeToUtf8Bytes(list[i], EngineState.Options);
            if (completedLength + item.Length + 1 > completed.Length) Array.Resize(ref completed, Math.Max(completed.Length * 2, completedLength + item.Length + 1));
            if (i > 0) completed[completedLength++] = (byte)',';
            item.CopyTo(completed, completedLength);
            completedLength += item.Length;
        }
        completedCount = list.Count;
        parts.Add(completed.AsMemory(0, completedLength));
        parts.Add(CloseArray);
    }

    // Each import summary once (they are replaced, never changed in place).
    private void AddImports(List<ReadOnlyMemory<byte>> parts, List<ImportRecord> records)
    {
        importsSpare.Clear();
        parts.Add(OpenArray);
        for (var i = 0; i < records.Count; i++)
        {
            if (!imports.TryGetValue(records[i], out var bytes))
            {
                bytes = JsonSerializer.SerializeToUtf8Bytes(records[i], EngineState.Options);
                changed = true;
            }
            importsSpare[records[i]] = bytes;
            if (i > 0) parts.Add(Comma);
            parts.Add(bytes);
        }
        parts.Add(CloseArray);
        if (importsSpare.Count != imports.Count) changed = true;
        (imports, importsSpare) = (importsSpare, imports);
    }

    // Each stamp record once ("hash":{...}; a record is replaced, never changed in place).
    private void AddStamps(List<ReadOnlyMemory<byte>> parts, Dictionary<string, StampRecord> records)
    {
        stampsSpare.Clear();
        parts.Add(Open);
        var first = true;
        foreach (var (hash, record) in records)
        {
            if (!stamps.TryGetValue(record, out var known) || !string.Equals(known.Hash, hash, StringComparison.Ordinal))
            {
                var name = JsonSerializer.SerializeToUtf8Bytes(hash, EngineState.Options);
                var value = JsonSerializer.SerializeToUtf8Bytes(record, EngineState.Options);
                known = (hash, [.. name, (byte)':', .. value]);
                changed = true;
            }
            stampsSpare[record] = known;
            if (!first) parts.Add(Comma);
            first = false;
            parts.Add(known.Bytes);
        }
        parts.Add(Close);
        if (stampsSpare.Count != stamps.Count) changed = true;
        (stamps, stampsSpare) = (stampsSpare, stamps);
    }
}

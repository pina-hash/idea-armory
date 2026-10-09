using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Armory.Core;

// Format 1 of the badge sections that the running Armory publishes and ArmoryBadges.dll reads
// inside File Explorer. docs/agent/EXPLORER.md is the normative spec; native/badges/BadgeTable.h
// is the reader. Everything is little endian.
//
// Header section "Local\IDEA-Armory-Badges-<user SID>", 4096 bytes:
//   0 magic "ARBH", 4 version, 8 generation (int64; 0 means no table), 16 publisher process id,
//   20 reserved, 24 updatedAt (FILETIME UTC), 32 newest (the newest generation this header ever
//   named; publishers only), the rest zero.
// Table section "<header name>-<generation as 16 uppercase hex digits>", immutable:
//   0 magic "ARBT", 4 version, 8 generation, 16 entryCount, 20 slotCount, 24 rootOffset,
//   28 rootUnits, 32 stringsOffset, 36 stringsUnits, 40 totalBytes, 44 reserved (20 bytes),
//   64 slots (16 bytes each: uint64 hash, uint32 offset in units, uint16 units, uint8 state,
//   uint8 reserved; units 0 is an empty slot), then the folded root, then the string pool.
public static class BadgeTable
{
    public const uint HeaderMagic = 0x48425241;  // "ARBH"
    public const uint TableMagic = 0x54425241;   // "ARBT"
    public const uint FormatVersion = 1;
    public const int HeaderBytes = 4096;
    public const int HeaderGenerationOffset = 8;
    public const int HeaderPidOffset = 16;
    public const int HeaderUpdatedAtOffset = 24;
    public const int HeaderNewestOffset = 32;
    public const int TableHeaderBytes = 64;
    public const int SlotBytes = 16;
    public const int MinSlots = 16;
    public const int MaxRootUnits = 259;
    // Longer vault-relative paths are never in a table (VaultPath keeps whole paths to 240).
    public const int MaxPathUnits = 1024;

    // The table section's name for one generation.
    public static string SectionName(string headerName, long generation) =>
        headerName + "-" + generation.ToString("X16", CultureInfo.InvariantCulture);

    // One UTF-16 unit to one: ASCII a to z inline, every other unit through fold
    // (Armory.Platform.Windows.ShellFold, the same LCMapStringEx call the DLL makes).
    public static char Fold(char c, Func<char, char> fold) => c < 0x80 ? (c is >= 'a' and <= 'z' ? (char)(c - 32) : c) : fold(c);

    public static string Fold(ReadOnlySpan<char> text, Func<char, char> fold)
    {
        var folded = new StringBuilder(text.Length);
        foreach (var c in text) folded.Append(Fold(c, fold));
        return folded.ToString();
    }

    // FNV-1a over UTF-16 code units, one step per unit.
    public static ulong Hash(ReadOnlySpan<char> folded)
    {
        var hash = 14695981039346656037ul;
        foreach (var c in folded)
        {
            hash ^= c;
            hash = unchecked(hash * 1099511628211ul);
        }
        return hash;
    }

    // The table bytes for one generation. Paths are vault-relative (either separator); paths
    // that fold to the same key keep the strongest state; empty and over-long paths and
    // entries without a badge are left out. The same input always gives the same bytes.
    public static byte[] Build(string vaultRoot, IEnumerable<BadgeEntry> entries, long generation, Func<char, char> fold) =>
        Build(vaultRoot, entries, generation, fold, Hash);

    // hash is replaceable for tests only (forced collisions).
    internal static byte[] Build(string vaultRoot, IEnumerable<BadgeEntry> entries, long generation, Func<char, char> fold, HashFunction hash)
    {
        if (generation == 0) throw new ArgumentOutOfRangeException(nameof(generation), "Generation 0 means no table.");
        var root = Fold(vaultRoot.Replace('/', '\\').TrimEnd('\\'), fold);
        if (root.Length is 0 or > MaxRootUnits) throw new ArgumentException("The vault root must be 1 to 259 characters.", nameof(vaultRoot));
        var states = new Dictionary<string, BadgeState>(StringComparer.Ordinal);
        foreach (var (path, state) in entries)
        {
            if (state == BadgeState.None) continue;
            if (!Enum.IsDefined(state)) throw new ArgumentException($"Unknown badge state {(byte)state}.", nameof(entries));
            var key = Fold(path.Replace('/', '\\').Trim('\\'), fold);
            if (key.Length is 0 or > MaxPathUnits) continue;
            if (!states.TryGetValue(key, out var had) || had < state) states[key] = state;
        }
        var slots = MinSlots;
        while (slots < states.Count * 2) slots *= 2;
        var keys = states.Keys.Order(StringComparer.Ordinal).ToArray();
        long stringsUnits = keys.Sum(k => (long)k.Length);
        long rootOffset = TableHeaderBytes + (long)slots * SlotBytes;
        var stringsOffset = rootOffset + root.Length * 2L;
        var total = stringsOffset + stringsUnits * 2;
        if (total > int.MaxValue) throw new ArgumentException("Too many badges for one table.", nameof(entries));
        var bytes = new byte[total];
        var span = bytes.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span, TableMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], FormatVersion);
        BinaryPrimitives.WriteInt64LittleEndian(span[8..], generation);
        BinaryPrimitives.WriteUInt32LittleEndian(span[16..], (uint)keys.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(span[20..], (uint)slots);
        BinaryPrimitives.WriteUInt32LittleEndian(span[24..], (uint)rootOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(span[28..], (uint)root.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(span[32..], (uint)stringsOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(span[36..], (uint)stringsUnits);
        BinaryPrimitives.WriteUInt32LittleEndian(span[40..], (uint)total);
        Encoding.Unicode.GetBytes(root, span[(int)rootOffset..]);
        var mask = slots - 1;
        var offset = 0;
        foreach (var key in keys)
        {
            Encoding.Unicode.GetBytes(key, span[(int)(stringsOffset + offset * 2L)..]);
            var h = hash(key);
            var i = (int)((uint)h & (uint)mask);
            while (BinaryPrimitives.ReadUInt16LittleEndian(span[(TableHeaderBytes + i * SlotBytes + 12)..]) != 0) i = (i + 1) & mask;
            var slot = span[(TableHeaderBytes + i * SlotBytes)..];
            BinaryPrimitives.WriteUInt64LittleEndian(slot, h);
            BinaryPrimitives.WriteUInt32LittleEndian(slot[8..], (uint)offset);
            BinaryPrimitives.WriteUInt16LittleEndian(slot[12..], (ushort)key.Length);
            slot[14] = (byte)states[key];
            offset += key.Length;
        }
        return bytes;
    }

    internal delegate ulong HashFunction(ReadOnlySpan<char> folded);

    // True when table is a well-formed format 1 table of this generation, with every offset
    // inside it. The DLL makes the same checks before it reads a table.
    public static bool IsValid(ReadOnlySpan<byte> table, long generation) => TryView(table, generation, out _);

    // The badge for one full path as Explorer passes it ("C:\IDEA\Armory\Robot 2027\Plate.SLDPRT",
    // any case, a folder with or without its trailing backslash). Mirrors Lookup in
    // native/badges/BadgeTable.h step for step; an invalid table answers None.
    public static BadgeState Lookup(ReadOnlySpan<byte> table, long generation, string path, Func<char, char> fold) =>
        Lookup(table, generation, path, fold, Hash);

    internal static BadgeState Lookup(ReadOnlySpan<byte> table, long generation, string path, Func<char, char> fold, HashFunction hash)
    {
        if (!TryView(table, generation, out var view)) return BadgeState.None;
        // Native strings end at the first NUL.
        var end = path.IndexOf('\0');
        var text = end < 0 ? path.AsSpan() : path.AsSpan(0, end);
        // Outside the vault: a prefix compare and out.
        for (var i = 0; i < view.RootUnits; i++)
            if (i >= text.Length || Fold(text[i], fold) != view.Unit(table, view.RootOffset, i)) return BadgeState.None;
        if (text.Length <= view.RootUnits || text[view.RootUnits] != '\\') return BadgeState.None;
        var rest = text[(view.RootUnits + 1)..];
        if (rest.Length > MaxPathUnits) return BadgeState.None;
        Span<char> folded = stackalloc char[rest.Length];
        for (var i = 0; i < rest.Length; i++) folded[i] = Fold(rest[i], fold);
        var n = folded.Length;
        // A folder passed with its trailing backslash names the same folder.
        if (n > 0 && folded[n - 1] == '\\') n--;
        if (n == 0) return BadgeState.None;
        var key = folded[..n];
        var h = hash(key);
        var mask = view.SlotCount - 1;
        for (uint i = (uint)h & mask, probes = 0; probes <= mask; i = (i + 1) & mask, probes++)
        {
            var slot = table.Slice(TableHeaderBytes + (int)i * SlotBytes, SlotBytes);
            var units = BinaryPrimitives.ReadUInt16LittleEndian(slot[12..]);
            if (units == 0) return BadgeState.None;
            if (BinaryPrimitives.ReadUInt64LittleEndian(slot) != h || units != n) continue;
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(slot[8..]);
            if ((ulong)offset + units > (ulong)view.StringsUnits) return BadgeState.None;
            var same = true;
            for (var u = 0; u < units && same; u++) same = view.Unit(table, view.StringsOffset, (int)offset + u) == key[u];
            if (same) return slot[14] <= (byte)BadgeState.Attention ? (BadgeState)slot[14] : BadgeState.None;
        }
        return BadgeState.None;
    }

    // The first 40 bytes of the header section, generation included. A publisher writes the
    // generation last, with one aligned 64-bit store.
    public static void WriteHeader(Span<byte> header, long generation, uint publisherPid, long updatedAtFileTime, long newest = 0)
    {
        if (header.Length < HeaderBytes) throw new ArgumentException("The header is 4096 bytes.", nameof(header));
        BinaryPrimitives.WriteUInt32LittleEndian(header, HeaderMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], FormatVersion);
        BinaryPrimitives.WriteInt64LittleEndian(header[HeaderGenerationOffset..], generation);
        BinaryPrimitives.WriteUInt32LittleEndian(header[HeaderPidOffset..], publisherPid);
        BinaryPrimitives.WriteUInt32LittleEndian(header[20..], 0);
        BinaryPrimitives.WriteInt64LittleEndian(header[HeaderUpdatedAtOffset..], updatedAtFileTime);
        BinaryPrimitives.WriteInt64LittleEndian(header[HeaderNewestOffset..], Math.Max(newest, generation));
    }

    // The published generation, or 0 when header is not a format 1 header.
    public static long HeaderGeneration(ReadOnlySpan<byte> header) =>
        IsHeader(header) ? BinaryPrimitives.ReadInt64LittleEndian(header[HeaderGenerationOffset..]) : 0;

    // The newest generation a header ever named (a publisher taking it over starts above it), or 0.
    public static long HeaderNewest(ReadOnlySpan<byte> header) =>
        IsHeader(header) ? Math.Max(BinaryPrimitives.ReadInt64LittleEndian(header[HeaderGenerationOffset..]), BinaryPrimitives.ReadInt64LittleEndian(header[HeaderNewestOffset..])) : 0;

    private static bool IsHeader(ReadOnlySpan<byte> header) =>
        header.Length >= HeaderBytes && BinaryPrimitives.ReadUInt32LittleEndian(header) == HeaderMagic &&
        BinaryPrimitives.ReadUInt32LittleEndian(header[4..]) == FormatVersion;

    private readonly record struct View(uint SlotCount, int RootOffset, int RootUnits, int StringsOffset, int StringsUnits)
    {
        internal char Unit(ReadOnlySpan<byte> table, int offset, int index) => (char)BinaryPrimitives.ReadUInt16LittleEndian(table[(offset + index * 2)..]);
    }

    private static bool TryView(ReadOnlySpan<byte> table, long generation, out View view)
    {
        view = default;
        if (table.Length < TableHeaderBytes) return false;
        if (BinaryPrimitives.ReadUInt32LittleEndian(table) != TableMagic || BinaryPrimitives.ReadUInt32LittleEndian(table[4..]) != FormatVersion) return false;
        if (generation == 0 || BinaryPrimitives.ReadInt64LittleEndian(table[8..]) != generation) return false;
        var entries = BinaryPrimitives.ReadUInt32LittleEndian(table[16..]);
        var slots = BinaryPrimitives.ReadUInt32LittleEndian(table[20..]);
        var rootOffset = BinaryPrimitives.ReadUInt32LittleEndian(table[24..]);
        var rootUnits = BinaryPrimitives.ReadUInt32LittleEndian(table[28..]);
        var stringsOffset = BinaryPrimitives.ReadUInt32LittleEndian(table[32..]);
        var stringsUnits = BinaryPrimitives.ReadUInt32LittleEndian(table[36..]);
        var total = BinaryPrimitives.ReadUInt32LittleEndian(table[40..]);
        if (total > (uint)table.Length) return false;
        if (slots < MinSlots || (slots & (slots - 1)) != 0 || entries > slots / 2) return false;
        if (TableHeaderBytes + (ulong)slots * SlotBytes > total) return false;
        if (rootUnits is 0 or > MaxRootUnits) return false;
        if ((ulong)rootOffset + rootUnits * 2ul > total || (rootOffset & 1) != 0) return false;
        if ((ulong)stringsOffset + stringsUnits * 2ul > total || (stringsOffset & 1) != 0) return false;
        view = new View(slots, (int)rootOffset, (int)rootUnits, (int)stringsOffset, (int)stringsUnits);
        return true;
    }
}

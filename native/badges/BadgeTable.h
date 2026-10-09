// IDEA Armory badge sections, format 1 (docs/agent/EXPLORER.md is the normative spec; the
// writer is Armory.Core.BadgeTable, and BadgeTableTests plus BadgeProbeTests keep the two equal).
//
// Two pagefile-backed named sections, created by the running IdeaArmory.exe for its own Windows
// user and session, read (never written) by ArmoryBadges.dll inside File Explorer:
//
//   Local\IDEA-Armory-Badges-<user SID>                       the header (4096 bytes)
//   Local\IDEA-Armory-Badges-<user SID>-<generation, 16 hex>  one immutable table
//
// Armory writes a whole new table section, then publishes its generation in the header with one
// aligned 64-bit store. The handler maps a table once per generation and only reads it.
// Everything is little endian; every offset is checked against the mapped size.
#pragma once
#include <windows.h>
#include <stdint.h>
#include <string.h>

namespace armory_badges {

constexpr uint32_t HeaderMagic = 0x48425241;  // "ARBH"
constexpr uint32_t TableMagic = 0x54425241;   // "ARBT"
constexpr uint32_t FormatVersion = 1;
constexpr uint32_t HeaderSize = 4096;
constexpr uint32_t TableHeaderSize = 64;
constexpr uint32_t SlotSize = 16;
constexpr uint32_t MaxRootUnits = 259;
constexpr uint32_t MaxPathUnits = 1024;       // longer relative paths are never in the table

// The badge an item shows; the value is also its strength (Armory.Core.BadgeState).
enum State : uint8_t {
    None = 0,
    Synced = 1,     // up to date, and not checked out by anyone
    Locked = 2,     // checked out by someone else, or by you on another computer
    Mine = 3,       // checked out by you on this computer, or new here and not in Armory yet
    Attention = 4,  // can't be uploaded, can't be read, changed without a check out, kept copy
};

#pragma pack(push, 1)
struct Header {
    uint32_t magic;               // HeaderMagic
    uint32_t version;             // FormatVersion
    volatile int64_t generation;  // 0: no table (Armory turned the badges off or is quitting)
    uint32_t armoryPid;           // the process that publishes; its exit drops every badge
    uint32_t reserved;
    uint64_t updatedAt;           // FILETIME (UTC) of the last publish, for diagnostics only
    int64_t newest;               // the newest generation ever named here (publishers only)
    // 40 bytes so far; the rest of the 4096 bytes is zero.
};

struct TableHeader {
    uint32_t magic;          // TableMagic
    uint32_t version;        // FormatVersion
    int64_t generation;      // equals the generation in the section's name
    uint32_t entryCount;
    uint32_t slotCount;      // a power of two, at least 16 and at least twice entryCount
    uint32_t rootOffset;     // bytes from the start of the section
    uint32_t rootUnits;      // the folded vault root, no trailing backslash ("C:\IDEA\ARMORY")
    uint32_t stringsOffset;  // bytes from the start of the section
    uint32_t stringsUnits;   // UTF-16 code units in the string pool
    uint32_t totalBytes;     // the section's meaningful length
    uint8_t reserved[20];
};

struct Slot {
    uint64_t hash;    // Fnv1a64 of the folded relative path
    uint32_t offset;  // in UTF-16 code units from the start of the string pool
    uint16_t units;   // length of the folded relative path ("ROBOT 2027\PLATE.SLDPRT"); 0 is empty
    uint8_t state;    // State
    uint8_t reserved;
};
#pragma pack(pop)

static_assert(sizeof(Header) == 40, "header layout");
static_assert(sizeof(TableHeader) == TableHeaderSize, "table header layout");
static_assert(sizeof(Slot) == SlotSize, "slot layout");

// Case folding shared with Armory (Armory.Platform.Windows.ShellFold calls the same function):
// ASCII a to z inline, any other code unit through LCMapStringEx(LOCALE_NAME_INVARIANT,
// LCMAP_UPPERCASE), one code unit at a time so the length never changes.
inline wchar_t Fold(wchar_t c) {
    if (c < 0x80) return (c >= L'a' && c <= L'z') ? static_cast<wchar_t>(c - 32) : c;
    wchar_t out = c;
    if (LCMapStringEx(LOCALE_NAME_INVARIANT, LCMAP_UPPERCASE, &c, 1, &out, 1, nullptr, nullptr, 0) != 1) return c;
    return out;
}

// FNV-1a over UTF-16 code units (each unit is one step, not two bytes).
inline uint64_t Fnv1a64(const wchar_t* folded, uint32_t units) {
    uint64_t h = 14695981039346656037ull;
    for (uint32_t i = 0; i < units; ++i) {
        h ^= static_cast<uint16_t>(folded[i]);
        h *= 1099511628211ull;
    }
    return h;
}

// The bounds of one mapped table, checked once when it is mapped and copied out of shared
// memory, so a lookup stays inside the view even if the section were changed later.
struct TableView {
    const uint8_t* base = nullptr;
    const Slot* slots = nullptr;
    const wchar_t* strings = nullptr;
    const wchar_t* root = nullptr;
    uint32_t mask = 0;
    uint32_t stringsUnits = 0;
    uint32_t rootUnits = 0;
};

inline bool Validate(const uint8_t* base, size_t mapped, int64_t expectedGeneration, TableView& view) {
    if (!base || mapped < TableHeaderSize || expectedGeneration == 0) return false;
    TableHeader t;
    memcpy(&t, base, sizeof t);
    if (t.magic != TableMagic || t.version != FormatVersion || t.generation != expectedGeneration) return false;
    if (t.totalBytes > mapped) return false;
    const uint32_t slots = t.slotCount;
    if (slots < 16 || (slots & (slots - 1)) != 0 || t.entryCount > slots / 2) return false;
    const uint64_t slotsEnd = TableHeaderSize + static_cast<uint64_t>(slots) * SlotSize;
    if (slotsEnd > t.totalBytes) return false;
    if (t.rootUnits == 0 || t.rootUnits > MaxRootUnits) return false;
    if (static_cast<uint64_t>(t.rootOffset) + t.rootUnits * 2ull > t.totalBytes || (t.rootOffset & 1)) return false;
    if (static_cast<uint64_t>(t.stringsOffset) + t.stringsUnits * 2ull > t.totalBytes || (t.stringsOffset & 1)) return false;
    view.base = base;
    view.slots = reinterpret_cast<const Slot*>(base + TableHeaderSize);
    view.strings = reinterpret_cast<const wchar_t*>(base + t.stringsOffset);
    view.root = reinterpret_cast<const wchar_t*>(base + t.rootOffset);
    view.mask = slots - 1;
    view.stringsUnits = t.stringsUnits;
    view.rootUnits = t.rootUnits;
    return true;
}

// The state of one full path (as Explorer passes it), or None. Never allocates, never blocks,
// never reads outside the checked bounds.
inline State Lookup(const TableView& v, const wchar_t* path) {
    const uint32_t r = v.rootUnits;
    // Outside the vault (the common case in any folder): a prefix check and out.
    for (uint32_t i = 0; i < r; ++i) {
        const wchar_t c = path[i];
        if (c == 0 || Fold(c) != v.root[i]) return None;
    }
    if (path[r] != L'\\') return None;
    const wchar_t* rest = path + r + 1;
    wchar_t folded[MaxPathUnits];
    uint32_t n = 0;
    for (; rest[n] != 0; ++n) {
        if (n == MaxPathUnits) return None;
        folded[n] = Fold(rest[n]);
    }
    // A trailing backslash (a folder passed as "...\Folder\") names the same folder.
    if (n > 0 && folded[n - 1] == L'\\') --n;
    if (n == 0) return None;
    const uint64_t h = Fnv1a64(folded, n);
    uint32_t i = static_cast<uint32_t>(h) & v.mask;
    for (uint32_t probes = 0; probes <= v.mask; ++probes, i = (i + 1) & v.mask) {
        Slot s;
        memcpy(&s, &v.slots[i], sizeof s);
        if (s.units == 0) return None;
        if (s.hash != h || s.units != n) continue;
        if (static_cast<uint64_t>(s.offset) + s.units > v.stringsUnits) return None;
        if (memcmp(v.strings + s.offset, folded, n * sizeof(wchar_t)) == 0)
            return s.state <= Attention ? static_cast<State>(s.state) : None;
    }
    return None;
}

}  // namespace armory_badges

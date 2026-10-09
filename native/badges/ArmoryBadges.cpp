// ArmoryBadges.dll: the four icon overlay handlers (Attention, Mine, Locked, Synced) that show
// IDEA Armory's status on file icons in File Explorer (docs/agent/EXPLORER.md).
//
// It only maps and reads the badge sections the running IdeaArmory.exe publishes (BadgeTable.h).
// It never touches the network, the vault, the Restart Manager or any file, never waits, never
// allocates on a call, and answers S_FALSE after a prefix compare outside the vault. With no
// Armory running for this Windows user, every answer is S_FALSE.
#include <windows.h>
#include <shlobj.h>
#include <sddl.h>
#include <strsafe.h>
#include <atomic>
#include <new>
#include "BadgeTable.h"

using namespace armory_badges;

namespace {

HMODULE g_module = nullptr;
std::atomic<long> g_objects{0};
std::atomic<long> g_locks{0};

// One handler per badge, in the order of their icons in ArmoryBadges.rc and of their key names
// (" IDEAArmory1Attention" ... " IDEAArmory4Synced").
constexpr int BadgeCount = 4;
const CLSID Clsids[BadgeCount] = {
    // {E26E19F2-515F-472F-AD4F-1B0293728CE2} Attention
    {0xE26E19F2, 0x515F, 0x472F, {0xAD, 0x4F, 0x1B, 0x02, 0x93, 0x72, 0x8C, 0xE2}},
    // {DB040D16-C118-4CDA-B616-DF9340A8BC9F} Mine
    {0xDB040D16, 0xC118, 0x4CDA, {0xB6, 0x16, 0xDF, 0x93, 0x40, 0xA8, 0xBC, 0x9F}},
    // {DF50E3A9-57B8-44AE-B690-257AFF283F97} Locked
    {0xDF50E3A9, 0x57B8, 0x44AE, {0xB6, 0x90, 0x25, 0x7A, 0xFF, 0x28, 0x3F, 0x97}},
    // {58F5F8D8-1041-43B9-B8DE-0EBEBCC29CF0} Synced
    {0x58F5F8D8, 0x1041, 0x43B9, {0xB8, 0xDE, 0x0E, 0xBE, 0xBC, 0xC2, 0x9C, 0xF0}},
};
const State States[BadgeCount] = {Attention, Mine, Locked, Synced};
// Lower is stronger. The table holds one state per path, so two of ours never claim one item;
// this only matters against other apps' badges.
const int Priorities[BadgeCount] = {0, 10, 20, 30};
const wchar_t* const SeenNames[BadgeCount] = {L"SeenAttention", L"SeenMine", L"SeenLocked", L"SeenSynced"};
// IShellIconOverlayIdentifier: {0C6C4200-C589-11D0-999A-00C04FD655E1}
const IID IidOverlay = {0x0C6C4200, 0xC589, 0x11D0, {0x99, 0x9A, 0x00, 0xC0, 0x4F, 0xD6, 0x55, 0xE1}};

// One view of Armory's sections per process, shared by every handler object and thread.
struct Cache {
    SRWLOCK lock = SRWLOCK_INIT;
    wchar_t headerName[200] = {};
    bool named = false;
    HANDLE headerMap = nullptr;
    const Header* header = nullptr;
    HANDLE tableMap = nullptr;
    TableView table{};
    int64_t generation = 0;
    HANDLE armory = nullptr;
    DWORD armoryPid = 0;
    ULONGLONG nextAttach = 0;
    ULONGLONG nextLiveness = 0;
} g_cache;

// While Armory is not running, calls return before taking the lock until this tick.
std::atomic<ULONGLONG> g_detachedUntil{0};

constexpr ULONGLONG AttachEveryMs = 2000;    // looks for a running Armory at most this often
constexpr ULONGLONG LivenessEveryMs = 1000;  // checks that the publisher still runs

// Local\IDEA-Armory-Badges-<user SID>. ARMORY_BADGES_SECTION names another header (tests).
bool NameSections(Cache& c) {
    if (c.named) return true;
    wchar_t chosen[ARRAYSIZE(c.headerName)];
    DWORD got = GetEnvironmentVariableW(L"ARMORY_BADGES_SECTION", chosen, static_cast<DWORD>(ARRAYSIZE(chosen)));
    if (got > 0 && got < ARRAYSIZE(chosen)) {
        if (FAILED(StringCchCopyW(c.headerName, ARRAYSIZE(c.headerName), chosen))) return false;
        c.named = true;
        return true;
    }
    HANDLE token = nullptr;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token)) return false;
    alignas(TOKEN_USER) BYTE buffer[256];
    DWORD size = 0;
    const bool ok = GetTokenInformation(token, TokenUser, buffer, static_cast<DWORD>(sizeof buffer), &size) != FALSE;
    CloseHandle(token);
    if (!ok) return false;
    LPWSTR sid = nullptr;
    if (!ConvertSidToStringSidW(reinterpret_cast<TOKEN_USER*>(buffer)->User.Sid, &sid)) return false;
    const HRESULT hr = StringCchPrintfW(c.headerName, ARRAYSIZE(c.headerName), L"Local\\IDEA-Armory-Badges-%s", sid);
    LocalFree(sid);
    if (FAILED(hr)) return false;
    c.named = true;
    return true;
}

void DropTable(Cache& c) {
    if (c.table.base) UnmapViewOfFile(c.table.base);
    if (c.tableMap) CloseHandle(c.tableMap);
    c.table = TableView{};
    c.tableMap = nullptr;
    c.generation = 0;
}

void DropAll(Cache& c) {
    DropTable(c);
    if (c.header) UnmapViewOfFile(c.header);
    if (c.headerMap) CloseHandle(c.headerMap);
    if (c.armory) CloseHandle(c.armory);
    c.header = nullptr;
    c.headerMap = nullptr;
    c.armory = nullptr;
    c.armoryPid = 0;
}

int64_t PublishedGeneration(const Header* h) {
    const int64_t g = h->generation;
    std::atomic_thread_fence(std::memory_order_acquire);
    return g;
}

// Called under the exclusive lock: attaches, checks the publisher, maps a new generation.
void Refresh(Cache& c, ULONGLONG now) {
    if (!c.header) {
        if (now < c.nextAttach) return;
        c.nextAttach = now + AttachEveryMs;
        g_detachedUntil.store(c.nextAttach, std::memory_order_relaxed);
        if (!NameSections(c)) return;
        c.headerMap = OpenFileMappingW(FILE_MAP_READ, FALSE, c.headerName);
        if (!c.headerMap) return;
        c.header = static_cast<const Header*>(MapViewOfFile(c.headerMap, FILE_MAP_READ, 0, 0, HeaderSize));
        if (!c.header || c.header->magic != HeaderMagic || c.header->version != FormatVersion) {
            DropAll(c);
            return;
        }
        g_detachedUntil.store(0, std::memory_order_relaxed);
        c.nextLiveness = 0;
    }
    if (now >= c.nextLiveness) {
        c.nextLiveness = now + LivenessEveryMs;
        const DWORD pid = c.header->armoryPid;
        if (pid != c.armoryPid) {
            if (c.armory) CloseHandle(c.armory);
            c.armory = pid ? OpenProcess(SYNCHRONIZE, FALSE, pid) : nullptr;
            c.armoryPid = pid;
        }
        if (!c.armory || WaitForSingleObject(c.armory, 0) != WAIT_TIMEOUT) {
            // Armory quit or crashed: no badge is better than a stale one.
            DropAll(c);
            c.nextAttach = now + AttachEveryMs;
            g_detachedUntil.store(c.nextAttach, std::memory_order_relaxed);
            return;
        }
    }
    const int64_t g = PublishedGeneration(c.header);
    if (g == c.generation) return;
    DropTable(c);
    if (g == 0) return;
    wchar_t name[ARRAYSIZE(c.headerName) + 20];
    const uint64_t u = static_cast<uint64_t>(g);
    if (FAILED(StringCchPrintfW(name, ARRAYSIZE(name), L"%s-%08X%08X", c.headerName,
            static_cast<unsigned>(u >> 32), static_cast<unsigned>(u & 0xFFFFFFFFu)))) return;
    HANDLE map = OpenFileMappingW(FILE_MAP_READ, FALSE, name);
    if (!map) return;  // replaced again already; the next call tries the newer one
    const auto base = static_cast<const uint8_t*>(MapViewOfFile(map, FILE_MAP_READ, 0, 0, 0));
    MEMORY_BASIC_INFORMATION info{};
    TableView view{};
    if (!base || VirtualQuery(base, &info, sizeof info) == 0 || !Validate(base, info.RegionSize, g, view)) {
        if (base) UnmapViewOfFile(base);
        CloseHandle(map);
        return;
    }
    c.tableMap = map;
    c.table = view;
    c.generation = g;
}

State Query(const wchar_t* path) {
    const ULONGLONG now = GetTickCount64();
    if (now < g_detachedUntil.load(std::memory_order_relaxed)) return None;
    Cache& c = g_cache;
    AcquireSRWLockShared(&c.lock);
    const bool fresh = c.header && now < c.nextLiveness && PublishedGeneration(c.header) == c.generation;
    if (fresh) {
        const State s = c.table.base ? Lookup(c.table, path) : None;
        ReleaseSRWLockShared(&c.lock);
        return s;
    }
    ReleaseSRWLockShared(&c.lock);
    AcquireSRWLockExclusive(&c.lock);
    Refresh(c, now);
    const State s = c.table.base ? Lookup(c.table, path) : None;
    ReleaseSRWLockExclusive(&c.lock);
    return s;
}

// Once per process and badge, inside explorer.exe only: the proof for Armory's Settings that
// Explorer loaded this badge (Windows loads only the first 11 handlers in its list).
std::atomic<long> g_seen[BadgeCount];

void NoteLoaded(int index) {
    if (g_seen[index].exchange(1) != 0) return;
    wchar_t image[MAX_PATH];
    const DWORD n = GetModuleFileNameW(nullptr, image, MAX_PATH);
    if (n == 0 || n >= MAX_PATH) return;
    const wchar_t* base = image + n;
    while (base > image && base[-1] != L'\\') --base;
    if (CompareStringOrdinal(base, -1, L"explorer.exe", -1, TRUE) != CSTR_EQUAL) return;
    FILETIME ft;
    GetSystemTimeAsFileTime(&ft);
    const ULONGLONG when = (static_cast<ULONGLONG>(ft.dwHighDateTime) << 32) | ft.dwLowDateTime;
    const DWORD pid = GetCurrentProcessId();
    RegSetKeyValueW(HKEY_CURRENT_USER, L"Software\\IDEA Armory\\Badges", SeenNames[index], REG_QWORD, &when, static_cast<DWORD>(sizeof when));
    RegSetKeyValueW(HKEY_CURRENT_USER, L"Software\\IDEA Armory\\Badges", L"ExplorerPid", REG_DWORD, &pid, static_cast<DWORD>(sizeof pid));
}

class Overlay final : public IShellIconOverlayIdentifier {
public:
    explicit Overlay(int index) : index_(index) { ++g_objects; }
    ~Overlay() { --g_objects; }
    Overlay(const Overlay&) = delete;
    Overlay& operator=(const Overlay&) = delete;

    IFACEMETHODIMP QueryInterface(REFIID riid, void** out) override {
        if (!out) return E_POINTER;
        if (IsEqualIID(riid, IID_IUnknown) || IsEqualIID(riid, IidOverlay)) {
            *out = static_cast<IShellIconOverlayIdentifier*>(this);
            AddRef();
            return S_OK;
        }
        *out = nullptr;
        return E_NOINTERFACE;
    }
    IFACEMETHODIMP_(ULONG) AddRef() override { return ++refs_; }
    IFACEMETHODIMP_(ULONG) Release() override {
        const ULONG left = --refs_;
        if (left == 0) delete this;
        return left;
    }

    IFACEMETHODIMP IsMemberOf(LPCWSTR path, DWORD) override {
        if (!path) return S_FALSE;
        return Query(path) == States[index_] ? S_OK : S_FALSE;
    }

    IFACEMETHODIMP GetOverlayInfo(LPWSTR iconFile, int cchMax, int* index, DWORD* flags) override {
        if (!iconFile || !index || !flags || cchMax <= 0) return E_POINTER;
        const DWORD n = GetModuleFileNameW(g_module, iconFile, static_cast<DWORD>(cchMax));
        if (n == 0 || n >= static_cast<DWORD>(cchMax)) return E_FAIL;
        *index = index_;  // the icon groups in ArmoryBadges.rc, in this order
        *flags = ISIOI_ICONFILE | ISIOI_ICONINDEX;
        NoteLoaded(index_);
        return S_OK;
    }

    IFACEMETHODIMP GetPriority(int* priority) override {
        if (!priority) return E_POINTER;
        *priority = Priorities[index_];
        return S_OK;
    }

private:
    std::atomic<ULONG> refs_{1};
    int index_;
};

class Factory final : public IClassFactory {
public:
    explicit Factory(int index) : index_(index) { ++g_objects; }
    ~Factory() { --g_objects; }
    Factory(const Factory&) = delete;
    Factory& operator=(const Factory&) = delete;

    IFACEMETHODIMP QueryInterface(REFIID riid, void** out) override {
        if (!out) return E_POINTER;
        if (IsEqualIID(riid, IID_IUnknown) || IsEqualIID(riid, IID_IClassFactory)) {
            *out = static_cast<IClassFactory*>(this);
            AddRef();
            return S_OK;
        }
        *out = nullptr;
        return E_NOINTERFACE;
    }
    IFACEMETHODIMP_(ULONG) AddRef() override { return ++refs_; }
    IFACEMETHODIMP_(ULONG) Release() override {
        const ULONG left = --refs_;
        if (left == 0) delete this;
        return left;
    }
    IFACEMETHODIMP CreateInstance(IUnknown* outer, REFIID riid, void** out) override {
        if (!out) return E_POINTER;
        *out = nullptr;
        if (outer) return CLASS_E_NOAGGREGATION;
        auto overlay = new (std::nothrow) Overlay(index_);
        if (!overlay) return E_OUTOFMEMORY;
        const HRESULT hr = overlay->QueryInterface(riid, out);
        overlay->Release();
        return hr;
    }
    IFACEMETHODIMP LockServer(BOOL lock) override {
        if (lock) ++g_locks;
        else --g_locks;
        return S_OK;
    }

private:
    std::atomic<ULONG> refs_{1};
    int index_;
};

}  // namespace

extern "C" BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID reserved) {
    if (reason == DLL_PROCESS_ATTACH) {
        g_module = instance;
        DisableThreadLibraryCalls(instance);
    } else if (reason == DLL_PROCESS_DETACH && reserved == nullptr) {
        DropAll(g_cache);  // FreeLibrary only; at process exit the system cleans up
    }
    return TRUE;
}

extern "C" HRESULT __stdcall DllGetClassObject(REFCLSID clsid, REFIID riid, void** out) {
    if (!out) return E_POINTER;
    *out = nullptr;
    for (int i = 0; i < BadgeCount; ++i) {
        if (!IsEqualCLSID(clsid, Clsids[i])) continue;
        auto factory = new (std::nothrow) Factory(i);
        if (!factory) return E_OUTOFMEMORY;
        const HRESULT hr = factory->QueryInterface(riid, out);
        factory->Release();
        return hr;
    }
    return CLASS_E_CLASSNOTAVAILABLE;
}

extern "C" HRESULT __stdcall DllCanUnloadNow() {
    return g_objects.load() == 0 && g_locks.load() == 0 ? S_OK : S_FALSE;
}
